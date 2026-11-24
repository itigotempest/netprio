use crate::config::PriorityConfig;
use libloading::{Library, Symbol};
use std::collections::{HashMap, HashSet};
use std::ffi::{c_char, c_void, CString};
use std::mem::size_of;
use std::process::Command;
use std::sync::atomic::{AtomicBool, AtomicU64, AtomicU8, Ordering};
use std::sync::{Arc, RwLock};
use std::thread;
use std::time::{Duration, Instant};
use windows_sys::Win32::Foundation::{CloseHandle, HANDLE, INVALID_HANDLE_VALUE};
use windows_sys::Win32::NetworkManagement::IpHelper::{
    GetExtendedTcpTable, GetExtendedUdpTable, MIB_TCPTABLE_OWNER_PID, MIB_UDPTABLE_OWNER_PID,
    TCP_TABLE_OWNER_PID_ALL, UDP_TABLE_OWNER_PID,
};
use windows_sys::Win32::Networking::WinSock::AF_INET;
use windows_sys::Win32::System::Diagnostics::ToolHelp::{
    CreateToolhelp32Snapshot, Process32FirstW, Process32NextW, PROCESSENTRY32W, TH32CS_SNAPPROCESS,
};

#[link(name = "winmm")]
unsafe extern "system" {
    fn timeBeginPeriod(uPeriod: u32) -> u32;
    fn timeEndPeriod(uPeriod: u32) -> u32;
}

const TIER_NONE: u8 = 0;
const TIER_HIGH: u8 = 1;
const TIER_MED: u8 = 2;
const TIER_LOW: u8 = 3;

const WINDIVERT_FLAG_SNIFF: u64 = 1;

#[repr(C)]
#[derive(Clone, Copy)]
struct WinDivertAddress {
    timestamp: i64,
    layer: u8,
    event: u8,
    flags: u8,
    _pad1: u8,
    _pad2: u32,
    if_idx: u32,
    sub_if_idx: u32,
    _data: [u8; 56],
}

impl Default for WinDivertAddress {
    fn default() -> Self {
        unsafe { std::mem::zeroed() }
    }
}

impl WinDivertAddress {
    #[inline(always)]
    fn is_outbound(&self) -> bool {
        (self.flags & 0x02) != 0
    }
}

#[derive(Debug, Clone, Default)]
pub struct TrafficStats {
    pub high_mbps: f64,
    pub med_mbps: f64,
    pub low_mbps: f64,
    pub low_limit_mbps: f64,
    pub med_limit_mbps: f64,
    pub is_low_capped: bool,
    pub is_med_capped: bool,
    pub error: Option<String>,
}

type FnOpen = unsafe extern "C" fn(*const c_char, i32, i16, u64) -> isize;
type FnRecvEx = unsafe extern "C" fn(isize, *mut u8, u32, *mut u32, u64, *mut WinDivertAddress, *mut u32, *mut c_void) -> i32;
type FnSendEx = unsafe extern "C" fn(isize, *const u8, u32, *mut u32, u64, *const WinDivertAddress, u32, *mut c_void) -> i32;
type FnCalcChecksums = unsafe extern "C" fn(*mut u8, u32, *mut WinDivertAddress, u64) -> i32;
type FnClose = unsafe extern "C" fn(isize) -> i32;

struct PortTierTable {
    tcp: Box<[AtomicU8]>,
    udp: Box<[AtomicU8]>,
}

impl PortTierTable {
    fn new() -> Self {
        Self {
            tcp: (0..65536).map(|_| AtomicU8::new(TIER_NONE)).collect(),
            udp: (0..65536).map(|_| AtomicU8::new(TIER_NONE)).collect(),
        }
    }
}

pub struct ShaperEngine {
    config: Arc<RwLock<PriorityConfig>>,
    running: Arc<AtomicBool>,
    skip_csum: Arc<AtomicBool>,
    need_shaping: Arc<AtomicBool>,
    stats: Arc<RwLock<TrafficStats>>,
    divert_handle: Arc<AtomicU64>,
    sniff_handle: Arc<AtomicU64>,
}

impl ShaperEngine {
    pub fn new(config: PriorityConfig) -> Self {
        let skip_csum = config.skip_checksum_recalc;
        Self {
            config: Arc::new(RwLock::new(config)),
            running: Arc::new(AtomicBool::new(false)),
            skip_csum: Arc::new(AtomicBool::new(skip_csum)),
            need_shaping: Arc::new(AtomicBool::new(false)),
            stats: Arc::new(RwLock::new(TrafficStats::default())),
            divert_handle: Arc::new(AtomicU64::new(0)),
            sniff_handle: Arc::new(AtomicU64::new(0)),
        }
    }

    pub fn update_config(&self, cfg: PriorityConfig) {
        self.skip_csum.store(cfg.skip_checksum_recalc, Ordering::Relaxed);
        if let Ok(mut guard) = self.config.write() {
            *guard = cfg;
        }
    }

    pub fn is_running(&self) -> bool {
        self.running.load(Ordering::Relaxed)
    }

    pub fn get_stats(&self) -> TrafficStats {
        self.stats.read().map(|s| s.clone()).unwrap_or_default()
    }

    pub fn start(&self) {
        if self.running.swap(true, Ordering::SeqCst) {
            return;
        }

        let lib = match load_windivert_library() {
            Ok(l) => Arc::new(l),
            Err(e) => {
                self.running.store(false, Ordering::SeqCst);
                if let Ok(mut s) = self.stats.write() {
                    s.error = Some(format!("WinDivert.dll error: {e}"));
                }
                return;
            }
        };

        let open: Symbol<FnOpen> = unsafe { lib.get(b"WinDivertOpen\0").unwrap() };
        let f_sniff = CString::new("loopback and ip and (tcp or udp)").unwrap();
        let h_sniff = unsafe { open(f_sniff.as_ptr(), 0, 1, WINDIVERT_FLAG_SNIFF) };
        self.sniff_handle.store(if h_sniff > 0 { h_sniff as u64 } else { 0 }, Ordering::SeqCst);

        let port_table = Arc::new(PortTierTable::new());
        let med_running = Arc::new(AtomicBool::new(false));
        let high_bytes = Arc::new(AtomicU64::new(0));
        let med_bytes = Arc::new(AtomicU64::new(0));
        let low_bytes = Arc::new(AtomicU64::new(0));
        let low_limit_bps = Arc::new(AtomicU64::new(u64::MAX));
        let med_limit_bps = Arc::new(AtomicU64::new(u64::MAX));

        // 1. Lock-free Process & Port Table Updater
        {
            let running = self.running.clone();
            let cfg = self.config.clone();
            let pt = port_table.clone();
            let med_run = med_running.clone();
            thread::spawn(move || {
                while running.load(Ordering::Relaxed) {
                    let (pid_tier, any_med) = {
                        let c = cfg.read().unwrap();
                        snapshot_pid_tiers(&c)
                    };
                    med_run.store(any_med, Ordering::Relaxed);
                    update_tcp_tiers(&pid_tier, &pt.tcp);
                    update_udp_tiers(&pid_tier, &pt.udp);
                    thread::sleep(Duration::from_millis(1500));
                }
            });
        }

        // 2. Adaptive Control Loop (switches main handle between SNIFF and SHAPE)
        {
            let lib_c = lib.clone();
            let running = self.running.clone();
            let cfg = self.config.clone();
            let stats = self.stats.clone();
            let med_run = med_running.clone();
            let need_shaping = self.need_shaping.clone();
            let divert_handle = self.divert_handle.clone();
            let hb = high_bytes.clone();
            let mb = med_bytes.clone();
            let lb = low_bytes.clone();
            let low_lim = low_limit_bps.clone();
            let med_lim = med_limit_bps.clone();

            thread::spawn(move || {
                let mut ema_high = 0.0_f64;
                let mut ema_med = 0.0_f64;
                let init_total = cfg.read().map(|c| c.channel_download_mbps.max(1.0)).unwrap_or(100.0);
                let mut cur_low = init_total;
                let mut cur_med = init_total;
                let mut idle_ticks = 0u32;

                while running.load(Ordering::Relaxed) {
                    thread::sleep(Duration::from_millis(250));
                    let h = hb.swap(0, Ordering::Relaxed) as f64;
                    let m = mb.swap(0, Ordering::Relaxed) as f64;
                    let l = lb.swap(0, Ordering::Relaxed) as f64;

                    let inst_high = (h * 8.0) / (0.25 * 1_000_000.0);
                    let inst_med = (m * 8.0) / (0.25 * 1_000_000.0);
                    let inst_low = (l * 8.0) / (0.25 * 1_000_000.0);

                    ema_high = 0.55 * inst_high + 0.45 * ema_high;
                    ema_med = 0.55 * inst_med + 0.45 * ema_med;

                    let (total, floor, med_pct, low_pct) = {
                        let c = cfg.read().unwrap();
                        (
                            c.channel_download_mbps.max(1.0),
                            c.minimum_floor_mbps.clamp(0.5, c.channel_download_mbps.max(1.0)),
                            c.medium_cap_percent as f64 / 100.0,
                            c.low_cap_percent as f64 / 100.0,
                        )
                    };

                    let high_spike = inst_high > 1.2;
                    let high_busy = ema_high > 0.35;

                    let med_target = if high_spike {
                        (total * med_pct).max(floor)
                    } else if high_busy {
                        (total - ema_high * 1.30).clamp(floor, total)
                    } else {
                        total
                    };

                    let max_low_ceil = if med_run.load(Ordering::Relaxed) { total * 0.85 } else { total };
                    let low_target = if high_spike {
                        (total * low_pct).max(floor.min(max_low_ceil))
                    } else if high_busy || ema_med > 0.35 {
                        let used = ema_high * 1.30 + ema_med * 1.15;
                        (max_low_ceil - used).clamp(floor.min(max_low_ceil), max_low_ceil)
                    } else {
                        max_low_ceil
                    };

                    let step_up = total * 0.04;
                    cur_med = if med_target < cur_med { med_target } else { med_target.min(cur_med + step_up) };
                    cur_low = if low_target < cur_low { low_target } else { low_target.min(cur_low + step_up) };

                    let cap_med = cur_med < total * 0.98;
                    let cap_low = cur_low < total * 0.98;

                    med_lim.store(if cap_med { ((cur_med * 1_000_000.0) / 8.0) as u64 } else { u64::MAX }, Ordering::Relaxed);
                    low_lim.store(if cap_low { ((cur_low * 1_000_000.0) / 8.0) as u64 } else { u64::MAX }, Ordering::Relaxed);

                    let should_shape = cap_med || cap_low;
                    if should_shape {
                        idle_ticks = 0;
                        if !need_shaping.swap(true, Ordering::SeqCst) {
                            let old = divert_handle.swap(0, Ordering::SeqCst) as isize;
                            if old > 0 {
                                if let Ok(close) = unsafe { lib_c.get::<FnClose>(b"WinDivertClose\0") } {
                                    unsafe { close(old); }
                                }
                            }
                        }
                    } else if need_shaping.load(Ordering::Relaxed) {
                        idle_ticks += 1;
                        // 3 seconds hysteresis before dropping back to zero-overhead SNIFF mode
                        if idle_ticks >= 12 {
                            need_shaping.store(false, Ordering::SeqCst);
                            let old = divert_handle.swap(0, Ordering::SeqCst) as isize;
                            if old > 0 {
                                if let Ok(close) = unsafe { lib_c.get::<FnClose>(b"WinDivertClose\0") } {
                                    unsafe { close(old); }
                                }
                            }
                        }
                    }

                    if let Ok(mut s) = stats.write() {
                        *s = TrafficStats {
                            high_mbps: (ema_high * 10.0).round() / 10.0,
                            med_mbps: (ema_med * 10.0).round() / 10.0,
                            low_mbps: (inst_low * 10.0).round() / 10.0,
                            low_limit_mbps: (cur_low * 10.0).round() / 10.0,
                            med_limit_mbps: (cur_med * 10.0).round() / 10.0,
                            is_low_capped: cap_low,
                            is_med_capped: cap_med,
                            error: None,
                        };
                    }
                }
            });
        }

        // 3. Zero-lock Loopback SNIFF Thread
        if h_sniff > 0 {
            let lib_s = lib.clone();
            let running = self.running.clone();
            let pt = port_table.clone();
            let hb = high_bytes.clone();
            let mb = med_bytes.clone();

            thread::spawn(move || {
                let recv_ex: Symbol<FnRecvEx> = unsafe { lib_s.get(b"WinDivertRecvEx\0").unwrap() };
                const BATCH: usize = 64;
                let mut buf = vec![0u8; BATCH * 2048];
                let mut addrs = vec![WinDivertAddress::default(); BATCH];

                while running.load(Ordering::Relaxed) {
                    let mut recv_len = 0u32;
                    let mut addr_len = (BATCH * size_of::<WinDivertAddress>()) as u32;
                    let ok = unsafe {
                        recv_ex(h_sniff, buf.as_mut_ptr(), buf.len() as u32, &mut recv_len, 0, addrs.as_mut_ptr(), &mut addr_len, std::ptr::null_mut())
                    };
                    if ok == 0 { break; }

                    let count = addr_len as usize / size_of::<WinDivertAddress>();
                    let mut off = 0usize;

                    for _ in 0..count {
                        if off + 24 > recv_len as usize { break; }
                        let pkt_len = u16::from_be_bytes([buf[off + 2], buf[off + 3]]) as usize;
                        if pkt_len < 24 || off + pkt_len > recv_len as usize { break; }

                        let ihl = ((buf[off] & 0x0F) * 4) as usize;
                        let proto = buf[off + 9];
                        if pkt_len >= ihl + 4 {
                            let src_port = u16::from_be_bytes([buf[off + ihl], buf[off + ihl + 1]]) as usize;
                            let dst_port = u16::from_be_bytes([buf[off + ihl + 2], buf[off + ihl + 3]]) as usize;
                            let table = if proto == 6 { Some(&pt.tcp) } else if proto == 17 { Some(&pt.udp) } else { None };
                            if let Some(t) = table {
                                let t1 = t[src_port].load(Ordering::Relaxed);
                                let t2 = t[dst_port].load(Ordering::Relaxed);
                                if t1 == TIER_HIGH || t2 == TIER_HIGH {
                                    hb.fetch_add(pkt_len as u64, Ordering::Relaxed);
                                } else if t1 == TIER_MED || t2 == TIER_MED {
                                    mb.fetch_add(pkt_len as u64, Ordering::Relaxed);
                                }
                            }
                        }
                        off += pkt_len;
                    }
                    // Let WinDivert accumulate a full batch in kernel buffer (zero latency impact in SNIFF mode)
                    thread::sleep(Duration::from_millis(4));
                }
            });
        }

        // 4. Main PacketLoop (switches between SNIFF-only when idle and SHAPE when capped)
        {
            let lib_m = lib.clone();
            let running = self.running.clone();
            let skip_csum = self.skip_csum.clone();
            let need_shaping = self.need_shaping.clone();
            let divert_handle = self.divert_handle.clone();
            let stats = self.stats.clone();
            let pt = port_table;
            let hb = high_bytes;
            let mb = med_bytes;
            let lb = low_bytes;
            let low_lim = low_limit_bps;
            let med_lim = med_limit_bps;

            thread::spawn(move || {
                let open: Symbol<FnOpen> = unsafe { lib_m.get(b"WinDivertOpen\0").unwrap() };
                let recv_ex: Symbol<FnRecvEx> = unsafe { lib_m.get(b"WinDivertRecvEx\0").unwrap() };
                let send_ex: Symbol<FnSendEx> = unsafe { lib_m.get(b"WinDivertSendEx\0").unwrap() };
                let calc_csum: Symbol<FnCalcChecksums> = unsafe { lib_m.get(b"WinDivertHelperCalcChecksums\0").unwrap() };
                let close: Symbol<FnClose> = unsafe { lib_m.get(b"WinDivertClose\0").unwrap() };

                let f_main = CString::new("!loopback and ip and (tcp or udp)").unwrap();
                const BATCH: usize = 64;
                let mut recv_buf = vec![0u8; BATCH * 2048];
                let mut send_buf = vec![0u8; BATCH * 2048];
                let mut recv_addrs = vec![WinDivertAddress::default(); BATCH];
                let mut send_addrs = vec![WinDivertAddress::default(); BATCH];

                let mut low_tokens = 1_000_000.0_f64;
                let mut med_tokens = 1_000_000.0_f64;
                let mut last = Instant::now();

                while running.load(Ordering::Relaxed) {
                    let shaping = need_shaping.load(Ordering::Relaxed);
                    if shaping {
                        unsafe { timeBeginPeriod(1); }
                    }
                    let flags = if shaping { 0 } else { WINDIVERT_FLAG_SNIFF };
                    let h_main = unsafe { open(f_main.as_ptr(), 0, 0, flags) };
                    if h_main <= 0 {
                        running.store(false, Ordering::SeqCst);
                        if let Ok(mut s) = stats.write() {
                            s.error = Some("WinDivertOpen failed (Run as Administrator)".into());
                        }
                        break;
                    }
                    divert_handle.store(h_main as u64, Ordering::SeqCst);

                    while running.load(Ordering::Relaxed) && need_shaping.load(Ordering::Relaxed) == shaping {
                        let mut recv_len = 0u32;
                        let mut addr_len = (BATCH * size_of::<WinDivertAddress>()) as u32;
                        let ok = unsafe {
                            recv_ex(h_main, recv_buf.as_mut_ptr(), recv_buf.len() as u32, &mut recv_len, 0, recv_addrs.as_mut_ptr(), &mut addr_len, std::ptr::null_mut())
                        };
                        if ok == 0 { break; }

                        let now = Instant::now();
                        let dt = now.duration_since(last).as_secs_f64();
                        last = now;

                        let l_lim = low_lim.load(Ordering::Relaxed);
                        let m_lim = med_lim.load(Ordering::Relaxed);
                        if shaping {
                            if l_lim < u64::MAX {
                                let lf = l_lim as f64;
                                low_tokens = (low_tokens + dt * lf).min(lf);
                            }
                            if m_lim < u64::MAX {
                                let mf = m_lim as f64;
                                med_tokens = (med_tokens + dt * mf).min(mf);
                            }
                        }

                        let do_csum = shaping && !skip_csum.load(Ordering::Relaxed);
                        let count = addr_len as usize / size_of::<WinDivertAddress>();
                        let mut recv_off = 0usize;
                        let mut send_off = 0usize;
                        let mut send_count = 0usize;
                        let mut any_dropped = false;

                        for i in 0..count {
                            if recv_off + 24 > recv_len as usize { break; }
                            let pkt_len = u16::from_be_bytes([recv_buf[recv_off + 2], recv_buf[recv_off + 3]]) as usize;
                            if pkt_len < 24 || recv_off + pkt_len > recv_len as usize { break; }

                            let mut drop_pkt = false;
                            let ihl = ((recv_buf[recv_off] & 0x0F) * 4) as usize;
                            let proto = recv_buf[recv_off + 9];

                            if pkt_len >= ihl + 4 {
                                let local_port = if recv_addrs[i].is_outbound() {
                                    u16::from_be_bytes([recv_buf[recv_off + ihl], recv_buf[recv_off + ihl + 1]]) as usize
                                } else {
                                    u16::from_be_bytes([recv_buf[recv_off + ihl + 2], recv_buf[recv_off + ihl + 3]]) as usize
                                };

                                let tier = if proto == 6 {
                                    pt.tcp[local_port].load(Ordering::Relaxed)
                                } else if proto == 17 {
                                    pt.udp[local_port].load(Ordering::Relaxed)
                                } else {
                                    TIER_NONE
                                };

                                match tier {
                                    TIER_HIGH => {
                                        hb.fetch_add(pkt_len as u64, Ordering::Relaxed);
                                    }
                                    TIER_MED => {
                                        if shaping && m_lim < u64::MAX {
                                            if med_tokens >= pkt_len as f64 { med_tokens -= pkt_len as f64; }
                                            else { drop_pkt = true; }
                                        }
                                        if !drop_pkt { mb.fetch_add(pkt_len as u64, Ordering::Relaxed); }
                                    }
                                    TIER_LOW => {
                                        if shaping && l_lim < u64::MAX {
                                            if low_tokens >= pkt_len as f64 { low_tokens -= pkt_len as f64; }
                                            else { drop_pkt = true; }
                                        }
                                        if !drop_pkt { lb.fetch_add(pkt_len as u64, Ordering::Relaxed); }
                                    }
                                    _ => {}
                                }
                            }

                            if shaping {
                                if drop_pkt {
                                    if !any_dropped {
                                        if recv_off > 0 {
                                            send_buf[..recv_off].copy_from_slice(&recv_buf[..recv_off]);
                                            send_addrs[..send_count].copy_from_slice(&recv_addrs[..send_count]);
                                        }
                                        any_dropped = true;
                                    }
                                } else {
                                    if do_csum {
                                        unsafe {
                                            calc_csum(recv_buf.as_mut_ptr().add(recv_off), pkt_len as u32, &mut recv_addrs[i], 0);
                                        }
                                    }
                                    if any_dropped {
                                        send_buf[send_off..send_off + pkt_len].copy_from_slice(&recv_buf[recv_off..recv_off + pkt_len]);
                                        send_addrs[send_count] = recv_addrs[i];
                                    }
                                    send_off += pkt_len;
                                    send_count += 1;
                                }
                            }
                            recv_off += pkt_len;
                        }

                        if shaping && send_count > 0 {
                            let mut sent = 0u32;
                            let (buf_ptr, addr_ptr) = if any_dropped {
                                (send_buf.as_ptr(), send_addrs.as_ptr())
                            } else {
                                (recv_buf.as_ptr(), recv_addrs.as_ptr())
                            };
                            unsafe {
                                send_ex(h_main, buf_ptr, send_off as u32, &mut sent, 0, addr_ptr, (send_count * size_of::<WinDivertAddress>()) as u32, std::ptr::null_mut());
                            }
                            if count < 16 {
                                thread::sleep(Duration::from_millis(1));
                            }
                        } else if !shaping {
                            // Accumulate up to 64 packets per syscall in SNIFF mode (zero latency impact)
                            thread::sleep(Duration::from_millis(4));
                        }
                    }

                    if shaping {
                        unsafe { timeEndPeriod(1); }
                    }
                    let prev = divert_handle.swap(0, Ordering::SeqCst) as isize;
                    if prev > 0 {
                        unsafe { close(prev); }
                    }
                }
            });
        }
    }

    pub fn stop(&self) {
        if !self.running.swap(false, Ordering::SeqCst) {
            return;
        }
        if let Ok(lib) = load_windivert_library() {
            if let Ok(close) = unsafe { lib.get::<FnClose>(b"WinDivertClose\0") } {
                let h1 = self.divert_handle.swap(0, Ordering::SeqCst) as isize;
                if h1 > 0 { unsafe { close(h1); } }
                let h2 = self.sniff_handle.swap(0, Ordering::SeqCst) as isize;
                if h2 > 0 { unsafe { close(h2); } }
            }
        }
        let _ = Command::new("sc.exe").args(["stop", "WinDivert"]).output();
    }
}

fn load_windivert_library() -> Result<Library, libloading::Error> {
    if let Ok(exe) = std::env::current_exe() {
        if let Some(dir) = exe.parent() {
            let local_dll = dir.join("WinDivert.dll");
            if local_dll.exists() {
                return unsafe { Library::new(local_dll) };
            }
        }
    }

    #[cfg(has_windivert_embedded)]
    {
        let base = std::env::var_os("LOCALAPPDATA")
            .map(std::path::PathBuf::from)
            .unwrap_or_else(std::env::temp_dir)
            .join("NetPrio")
            .join("driver");
        let _ = std::fs::create_dir_all(&base);
        let dll_path = base.join("WinDivert.dll");
        let sys_path = base.join("WinDivert64.sys");
        let dll_bytes: &[u8] = include_bytes!("../WinDivert.dll");
        let sys_bytes: &[u8] = include_bytes!("../WinDivert64.sys");
        if !dll_path.exists() {
            let _ = std::fs::write(&dll_path, dll_bytes);
        }
        if !sys_path.exists() {
            let _ = std::fs::write(&sys_path, sys_bytes);
        }
        return unsafe { Library::new(dll_path) };
    }

    #[allow(unreachable_code)]
    unsafe { Library::new("WinDivert.dll") }
}

fn snapshot_pid_tiers(cfg: &PriorityConfig) -> (HashMap<u32, u8>, bool) {
    let mut pid_tier = HashMap::with_capacity(64);
    let mut any_med = false;

    let high_set: HashSet<String> = cfg.highest_apps.iter().map(|s| s.to_lowercase()).collect();
    let med_set: HashSet<String> = cfg.medium_apps.iter().map(|s| s.to_lowercase()).collect();
    let low_set: HashSet<String> = cfg.low_apps.iter().map(|s| s.to_lowercase()).collect();

    unsafe {
        let snap: HANDLE = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if snap == INVALID_HANDLE_VALUE || snap.is_null() {
            return (pid_tier, any_med);
        }
        let mut entry: PROCESSENTRY32W = std::mem::zeroed();
        entry.dwSize = size_of::<PROCESSENTRY32W>() as u32;
        if Process32FirstW(snap, &mut entry) != 0 {
            loop {
                let end = entry.szExeFile.iter().position(|&c| c == 0).unwrap_or(entry.szExeFile.len());
                let exe = String::from_utf16_lossy(&entry.szExeFile[..end]).to_lowercase();
                if high_set.contains(&exe) {
                    pid_tier.insert(entry.th32ProcessID, TIER_HIGH);
                } else if med_set.contains(&exe) {
                    pid_tier.insert(entry.th32ProcessID, TIER_MED);
                    any_med = true;
                } else if low_set.contains(&exe) {
                    pid_tier.insert(entry.th32ProcessID, TIER_LOW);
                }
                if Process32NextW(snap, &mut entry) == 0 {
                    break;
                }
            }
        }
        CloseHandle(snap);
    }
    (pid_tier, any_med)
}

fn update_tcp_tiers(pid_tier: &HashMap<u32, u8>, table_out: &[AtomicU8]) {
    let mut next = [TIER_NONE; 65536];
    let mut size = 0u32;
    unsafe {
        GetExtendedTcpTable(std::ptr::null_mut(), &mut size, 0, AF_INET as u32, TCP_TABLE_OWNER_PID_ALL, 0);
        if size > 0 {
            let mut buf = vec![0u8; size as usize];
            if GetExtendedTcpTable(buf.as_mut_ptr() as *mut c_void, &mut size, 0, AF_INET as u32, TCP_TABLE_OWNER_PID_ALL, 0) == 0 {
                let table = &*(buf.as_ptr() as *const MIB_TCPTABLE_OWNER_PID);
                let rows = std::slice::from_raw_parts(table.table.as_ptr(), table.dwNumEntries as usize);
                for r in rows {
                    if let Some(&t) = pid_tier.get(&r.dwOwningPid) {
                        let port = u16::from_be(r.dwLocalPort as u16) as usize;
                        next[port] = t;
                    }
                }
            }
        }
    }
    for (i, v) in next.iter().enumerate() {
        table_out[i].store(*v, Ordering::Relaxed);
    }
}

fn update_udp_tiers(pid_tier: &HashMap<u32, u8>, table_out: &[AtomicU8]) {
    let mut next = [TIER_NONE; 65536];
    let mut size = 0u32;
    unsafe {
        GetExtendedUdpTable(std::ptr::null_mut(), &mut size, 0, AF_INET as u32, UDP_TABLE_OWNER_PID, 0);
        if size > 0 {
            let mut buf = vec![0u8; size as usize];
            if GetExtendedUdpTable(buf.as_mut_ptr() as *mut c_void, &mut size, 0, AF_INET as u32, UDP_TABLE_OWNER_PID, 0) == 0 {
                let table = &*(buf.as_ptr() as *const MIB_UDPTABLE_OWNER_PID);
                let rows = std::slice::from_raw_parts(table.table.as_ptr(), table.dwNumEntries as usize);
                for r in rows {
                    if let Some(&t) = pid_tier.get(&r.dwOwningPid) {
                        let port = u16::from_be(r.dwLocalPort as u16) as usize;
                        next[port] = t;
                    }
                }
            }
        }
    }
    for (i, v) in next.iter().enumerate() {
        table_out[i].store(*v, Ordering::Relaxed);
    }
}
