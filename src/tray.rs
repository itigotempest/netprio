use std::sync::mpsc::Sender;
use std::thread;
use windows_sys::Win32::Foundation::{HWND, LPARAM, LRESULT, POINT, WPARAM};
use windows_sys::Win32::System::LibraryLoader::GetModuleHandleW;
use windows_sys::Win32::UI::Shell::{
    Shell_NotifyIconW, NIF_ICON, NIF_MESSAGE, NIF_TIP, NIM_ADD, NIM_DELETE, NOTIFYICONDATAW,
};
use windows_sys::Win32::UI::WindowsAndMessaging::{
    AppendMenuW, CreatePopupMenu, CreateWindowExW, DefWindowProcW, DestroyMenu, DispatchMessageW,
    GetCursorPos, GetMessageW, LoadIconW, PostQuitMessage, RegisterClassW, SetForegroundWindow,
    TrackPopupMenuEx, TranslateMessage, MF_SEPARATOR, MF_STRING, MSG, TPM_RETURNCMD,
    WM_LBUTTONDBLCLK, WM_LBUTTONUP, WM_RBUTTONUP, WNDCLASSW,
};

const WM_TRAY: u32 = 0x8001;
const ID_OPEN: usize = 1001;
const ID_TOGGLE: usize = 1002;
const ID_EXIT: usize = 1003;

#[derive(Debug, Clone, Copy)]
pub enum TrayEvent {
    Open,
    ToggleShaper,
    Exit,
}

static mut TRAY_TX: Option<Sender<TrayEvent>> = None;

pub fn spawn_tray(tx: Sender<TrayEvent>) {
    thread::spawn(move || unsafe {
        TRAY_TX = Some(tx);
        let hinst = GetModuleHandleW(std::ptr::null());
        let cls_name: Vec<u16> = "NetPrioTrayCls\0".encode_utf16().collect();

        let wc = WNDCLASSW {
            lpfnWndProc: Some(wnd_proc),
            hInstance: hinst,
            lpszClassName: cls_name.as_ptr(),
            ..std::mem::zeroed()
        };
        RegisterClassW(&wc);

        let hwnd = CreateWindowExW(
            0,
            cls_name.as_ptr(),
            cls_name.as_ptr(),
            0,
            0, 0, 0, 0,
            std::ptr::null_mut(),
            std::ptr::null_mut(),
            hinst,
            std::ptr::null(),
        );

        let hicon = LoadIconW(hinst, 1 as *const u16);
        let mut nid: NOTIFYICONDATAW = std::mem::zeroed();
        nid.cbSize = std::mem::size_of::<NOTIFYICONDATAW>() as u32;
        nid.hWnd = hwnd;
        nid.uID = 1;
        nid.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        nid.uCallbackMessage = WM_TRAY;
        nid.hIcon = hicon;
        let tip: Vec<u16> = "NetPrio".encode_utf16().collect();
        nid.szTip[..tip.len()].copy_from_slice(&tip);

        Shell_NotifyIconW(NIM_ADD, &nid);

        let mut msg: MSG = std::mem::zeroed();
        while GetMessageW(&mut msg, std::ptr::null_mut(), 0, 0) > 0 {
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }

        Shell_NotifyIconW(NIM_DELETE, &nid);
    });
}

unsafe extern "system" fn wnd_proc(hwnd: HWND, msg: u32, wparam: WPARAM, lparam: LPARAM) -> LRESULT {
    unsafe {
    if msg == WM_TRAY {
        let ev = (lparam & 0xFFFF) as u32;
        if ev == WM_LBUTTONUP || ev == WM_LBUTTONDBLCLK {
            if let Some(tx) = &*std::ptr::addr_of!(TRAY_TX) {
                let _ = tx.send(TrayEvent::Open);
            }
            return 0;
        } else if ev == WM_RBUTTONUP {
            let hmenu = CreatePopupMenu();
            let s_open: Vec<u16> = "Open NetPrio\0".encode_utf16().collect();
            let s_tog: Vec<u16> = "Start / Stop Shaper\0".encode_utf16().collect();
            let s_exit: Vec<u16> = "Exit\0".encode_utf16().collect();
            AppendMenuW(hmenu, MF_STRING, ID_OPEN, s_open.as_ptr());
            AppendMenuW(hmenu, MF_STRING, ID_TOGGLE, s_tog.as_ptr());
            AppendMenuW(hmenu, MF_SEPARATOR, 0, std::ptr::null());
            AppendMenuW(hmenu, MF_STRING, ID_EXIT, s_exit.as_ptr());

            let mut pt = POINT { x: 0, y: 0 };
            GetCursorPos(&mut pt);
            SetForegroundWindow(hwnd);
            let cmd = TrackPopupMenuEx(hmenu, TPM_RETURNCMD, pt.x, pt.y, hwnd, std::ptr::null_mut()) as usize;
            DestroyMenu(hmenu);

            if let Some(tx) = &*std::ptr::addr_of!(TRAY_TX) {
                if cmd == ID_OPEN { let _ = tx.send(TrayEvent::Open); }
                else if cmd == ID_TOGGLE { let _ = tx.send(TrayEvent::ToggleShaper); }
                else if cmd == ID_EXIT {
                    let _ = tx.send(TrayEvent::Exit);
                    PostQuitMessage(0);
                }
            }
            return 0;
        }
    }
    DefWindowProcW(hwnd, msg, wparam, lparam)
    }
}
