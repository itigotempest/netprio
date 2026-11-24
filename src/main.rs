#![windows_subsystem = "windows"]

mod config;
mod i18n;
mod shaper;
mod startup;
mod tray;

use config::PriorityConfig;
use i18n::t;
use iced::widget::{button, column, container, pick_list, row, scrollable, slider, text, text_input, toggler, Space};
use iced::{color, Element, Length, Subscription, Task};
use iced_fluent_theme::{BrandVariants, Theme as FluentTheme};
use shaper::{ShaperEngine, TrafficStats};
use std::sync::{mpsc, Arc, Mutex};
use std::time::Duration;
use tray::TrayEvent;

const BRAND: BrandVariants = BrandVariants {
    b10: color!(0x020305),
    b20: color!(0x121725),
    b30: color!(0x192542),
    b40: color!(0x1D305A),
    b50: color!(0x1F3C73),
    b60: color!(0x21498D),
    b70: color!(0x2156A8),
    b80: color!(0x1E63C4),
    b90: color!(0x1970E1),
    b100: color!(0x0C7EFE),
    b110: color!(0x4D8CFF),
    b120: color!(0x6F9AFF),
    b130: color!(0x89A9FF),
    b140: color!(0xA1B8FF),
    b150: color!(0xB7C7FF),
    b160: color!(0xCCD6FF),
};

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum Tab {
    Priorities,
    Settings,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum Tier {
    High,
    Med,
    Low,
}

#[derive(Debug, Clone)]
enum Message {
    SwitchTab(Tab),
    ToggleShaper,
    Tick,
    InputChanged(Tier, String),
    AddExe(Tier),
    RemoveExe(Tier, usize),
    MoveExe(Tier, Tier, usize),
    LangSelected(String),
    AutoStartToggled(bool),
    CloseToTrayToggled(bool),
    SkipChecksumToggled(bool),
    MedCapChanged(u32),
    LowCapChanged(u32),
    DownloadChanged(String),
    MeasureSpeed,
    SpeedMeasured(Result<f64, String>),
    SaveSettings,
    CloseRequested(iced::window::Id),
}

struct NetPrioApp {
    config: PriorityConfig,
    shaper: Arc<ShaperEngine>,
    tab: Tab,
    stats: TrafficStats,
    input_high: String,
    input_med: String,
    input_low: String,
    download_str: String,
    autostart: bool,
    status_note: Option<String>,
    tray_rx: Arc<Mutex<mpsc::Receiver<TrayEvent>>>,
    main_window: Option<iced::window::Id>,
}

impl NetPrioApp {
    fn new(start_minimized: bool) -> (Self, Task<Message>) {
        let config = PriorityConfig::load();
        let shaper = Arc::new(ShaperEngine::new(config.clone()));
        let (tx, rx) = mpsc::channel();
        tray::spawn_tray(tx);

        if start_minimized {
            shaper.start();
        }

        let download_str = config.channel_download_mbps.to_string();
        let app = Self {
            config,
            shaper,
            tab: Tab::Priorities,
            stats: TrafficStats::default(),
            input_high: String::new(),
            input_med: String::new(),
            input_low: String::new(),
            download_str,
            autostart: startup::is_enabled(),
            status_note: None,
            tray_rx: Arc::new(Mutex::new(rx)),
            main_window: None,
        };

        let task = if start_minimized {
            Task::none()
        } else {
            Task::none()
        };
        (app, task)
    }

    fn theme(&self) -> iced::Theme {
        let _fluent = FluentTheme::dark(Some(BRAND));
        iced::Theme::Dark
    }

    fn update(&mut self, msg: Message) -> Task<Message> {
        match msg {
            Message::SwitchTab(tab) => self.tab = tab,
            Message::ToggleShaper => {
                if self.shaper.is_running() {
                    self.shaper.stop();
                } else {
                    self.shaper.start();
                }
            }
            Message::Tick => {
                if self.shaper.is_running() {
                    self.stats = self.shaper.get_stats();
                }
                if let Ok(rx) = self.tray_rx.lock() {
                    while let Ok(ev) = rx.try_recv() {
                        match ev {
                            TrayEvent::Open => {
                                return iced::window::oldest().and_then(|id| {
                                    Task::batch([
                                        iced::window::set_mode(id, iced::window::Mode::Windowed),
                                        iced::window::gain_focus(id),
                                    ])
                                });
                            }
                            TrayEvent::ToggleShaper => {
                                if self.shaper.is_running() { self.shaper.stop(); }
                                else { self.shaper.start(); }
                            }
                            TrayEvent::Exit => {
                                self.shaper.stop();
                                return iced::exit();
                            }
                        }
                    }
                }
            }
            Message::InputChanged(tier, val) => match tier {
                Tier::High => self.input_high = val,
                Tier::Med => self.input_med = val,
                Tier::Low => self.input_low = val,
            },
            Message::AddExe(tier) => {
                let raw = match tier {
                    Tier::High => std::mem::take(&mut self.input_high),
                    Tier::Med => std::mem::take(&mut self.input_med),
                    Tier::Low => std::mem::take(&mut self.input_low),
                };
                let mut exe = raw.trim().to_string();
                if !exe.is_empty() {
                    if !exe.to_lowercase().ends_with(".exe") {
                        exe.push_str(".exe");
                    }
                    let list = match tier {
                        Tier::High => &mut self.config.highest_apps,
                        Tier::Med => &mut self.config.medium_apps,
                        Tier::Low => &mut self.config.low_apps,
                    };
                    if !list.iter().any(|x| x.eq_ignore_ascii_case(&exe)) {
                        list.push(exe);
                        self.config.save();
                        self.shaper.update_config(self.config.clone());
                    }
                }
            }
            Message::RemoveExe(tier, idx) => {
                let list = match tier {
                    Tier::High => &mut self.config.highest_apps,
                    Tier::Med => &mut self.config.medium_apps,
                    Tier::Low => &mut self.config.low_apps,
                };
                if idx < list.len() {
                    list.remove(idx);
                    self.config.save();
                    self.shaper.update_config(self.config.clone());
                }
            }
            Message::MoveExe(from, to, idx) => {
                let item = match from {
                    Tier::High if idx < self.config.highest_apps.len() => Some(self.config.highest_apps.remove(idx)),
                    Tier::Med if idx < self.config.medium_apps.len() => Some(self.config.medium_apps.remove(idx)),
                    Tier::Low if idx < self.config.low_apps.len() => Some(self.config.low_apps.remove(idx)),
                    _ => None,
                };
                if let Some(exe) = item {
                    let dst = match to {
                        Tier::High => &mut self.config.highest_apps,
                        Tier::Med => &mut self.config.medium_apps,
                        Tier::Low => &mut self.config.low_apps,
                    };
                    if !dst.iter().any(|x| x.eq_ignore_ascii_case(&exe)) {
                        dst.push(exe);
                    }
                    self.config.save();
                    self.shaper.update_config(self.config.clone());
                }
            }
            Message::LangSelected(lang) => {
                self.config.language = lang;
                self.config.save();
            }
            Message::AutoStartToggled(v) => {
                if startup::set_enabled(v) {
                    self.autostart = v;
                }
            }
            Message::CloseToTrayToggled(v) => {
                self.config.close_to_tray = v;
                self.config.save();
            }
            Message::SkipChecksumToggled(v) => {
                self.config.skip_checksum_recalc = v;
                self.config.save();
                self.shaper.update_config(self.config.clone());
            }
            Message::MedCapChanged(v) => self.config.medium_cap_percent = v,
            Message::LowCapChanged(v) => self.config.low_cap_percent = v,
            Message::DownloadChanged(s) => self.download_str = s,
            Message::MeasureSpeed => {
                self.status_note = Some("Measuring...".into());
                return Task::perform(measure_download_mbps(), Message::SpeedMeasured);
            }
            Message::SpeedMeasured(res) => match res {
                Ok(mbps) => {
                    self.config.channel_download_mbps = mbps;
                    self.download_str = mbps.to_string();
                    self.config.save();
                    self.shaper.update_config(self.config.clone());
                    self.status_note = Some(format!("{mbps} Mbps"));
                }
                Err(e) => self.status_note = Some(e),
            },
            Message::SaveSettings => {
                if let Ok(v) = self.download_str.trim().parse::<f64>() {
                    if v >= 1.0 {
                        self.config.channel_download_mbps = v;
                    }
                }
                self.config.save();
                self.shaper.update_config(self.config.clone());
                self.status_note = Some("Saved".into());
            }
            Message::CloseRequested(id) => {
                self.main_window = Some(id);
                if self.config.close_to_tray {
                    trim_memory();
                    return iced::window::set_mode(id, iced::window::Mode::Hidden);
                } else {
                    self.shaper.stop();
                    return iced::exit();
                }
            }
        }
        Task::none()
    }

    fn view(&self) -> Element<'_, Message> {
        let lang = &self.config.language;

        let nav = row![
            button(text(t("tab_priorities", lang))).on_press(Message::SwitchTab(Tab::Priorities)),
            button(text(t("tab_settings", lang))).on_press(Message::SwitchTab(Tab::Settings)),
            Space::new().width(Length::Fill),
            button(text(if self.shaper.is_running() { t("stop", lang) } else { t("start", lang) }))
                .on_press(Message::ToggleShaper),
        ]
        .spacing(12);

        let status_text = if let Some(err) = &self.stats.error {
            err.clone()
        } else if self.shaper.is_running() {
            let mbps = t("mbps", lang);
            let med_lim = if self.stats.is_med_capped {
                format!(" ({} {})", t("limit", lang), self.stats.med_limit_mbps)
            } else {
                String::new()
            };
            let low_lim = if self.stats.is_low_capped {
                format!("{} {} {}", t("limit", lang), self.stats.low_limit_mbps, mbps)
            } else {
                t("unlimited", lang).to_string()
            };
            format!(
                "{}: {} {}  |  {}: {} {}{}  |  {}: {} {} ({})",
                t("high", lang), self.stats.high_mbps, mbps,
                t("med", lang), self.stats.med_mbps, mbps, med_lim,
                t("low", lang), self.stats.low_mbps, mbps, low_lim
            )
        } else {
            t("status_stopped", lang).to_string()
        };

        let status_bar = container(text(status_text).size(14))
            .padding(12)
            .width(Length::Fill);

        let body: Element<'_, Message> = match self.tab {
            Tab::Priorities => {
                let col_high = self.tier_column(t("high", lang), Tier::High, &self.config.highest_apps, &self.input_high);
                let col_med = self.tier_column(t("med", lang), Tier::Med, &self.config.medium_apps, &self.input_med);
                let col_low = self.tier_column(t("low", lang), Tier::Low, &self.config.low_apps, &self.input_low);
                scrollable(row![col_high, col_med, col_low].spacing(16)).into()
            }
            Tab::Settings => {
                let langs = vec!["auto".to_string(), "ru".to_string(), "en".to_string()];
                let content = column![
                    row![
                        text(t("lang", lang)).width(Length::Fill),
                        pick_list(langs, Some(self.config.language.clone()), Message::LangSelected),
                    ].spacing(12),
                    toggler(self.autostart).label(t("autostart", lang)).on_toggle(Message::AutoStartToggled),
                    toggler(self.config.close_to_tray).label(t("close_tray", lang)).on_toggle(Message::CloseToTrayToggled),
                    row![
                        text(t("download_mbps", lang)).width(Length::Fill),
                        text_input("100", &self.download_str).on_input(Message::DownloadChanged).width(100),
                        button(text(t("measure", lang))).on_press(Message::MeasureSpeed),
                    ].spacing(12),
                    text(format!("{}: {}%", t("med_cap", lang), self.config.medium_cap_percent)),
                    slider(10..=90, self.config.medium_cap_percent, Message::MedCapChanged),
                    text(format!("{}: {}%", t("low_cap", lang), self.config.low_cap_percent)),
                    slider(5..=50, self.config.low_cap_percent, Message::LowCapChanged),
                    toggler(self.config.skip_checksum_recalc).label(t("skip_csum", lang)).on_toggle(Message::SkipChecksumToggled),
                    row![
                        button(text(t("save", lang))).on_press(Message::SaveSettings),
                        text(self.status_note.as_deref().unwrap_or("")),
                    ].spacing(12),
                ]
                .spacing(14)
                .max_width(560);
                scrollable(content).into()
            }
        };

        container(column![nav, status_bar, body].spacing(16))
            .padding(20)
            .width(Length::Fill)
            .height(Length::Fill)
            .into()
    }

    fn tier_column<'a>(&'a self, title: &'a str, tier: Tier, items: &'a [String], input_val: &'a str) -> Element<'a, Message> {
        let lang = &self.config.language;
        let mut list_col = column![].spacing(6);

        for (idx, exe) in items.iter().enumerate() {
            let mut item_row = row![text(exe).width(Length::Fill)].spacing(4);
            match tier {
                Tier::High => {
                    item_row = item_row.push(button("→").on_press(Message::MoveExe(Tier::High, Tier::Med, idx)));
                }
                Tier::Med => {
                    item_row = item_row
                        .push(button("←").on_press(Message::MoveExe(Tier::Med, Tier::High, idx)))
                        .push(button("→").on_press(Message::MoveExe(Tier::Med, Tier::Low, idx)));
                }
                Tier::Low => {
                    item_row = item_row.push(button("←").on_press(Message::MoveExe(Tier::Low, Tier::Med, idx)));
                }
            }
            item_row = item_row.push(button("✕").on_press(Message::RemoveExe(tier, idx)));
            list_col = list_col.push(item_row);
        }

        let add_row = row![
            text_input(t("add_placeholder", lang), input_val)
                .on_input(move |s| Message::InputChanged(tier, s))
                .on_submit(Message::AddExe(tier)),
            button(text(t("add_btn", lang))).on_press(Message::AddExe(tier)),
        ]
        .spacing(6);

        container(
            column![
                text(title).size(18),
                scrollable(list_col).height(Length::Fill),
                add_row,
            ]
            .spacing(12),
        )
        .padding(14)
        .width(Length::FillPortion(1))
        .height(Length::Fixed(420.0))
        .into()
    }

    fn subscription(&self) -> Subscription<Message> {
        Subscription::batch([
            iced::time::every(Duration::from_millis(400)).map(|_| Message::Tick),
            iced::window::close_requests().map(Message::CloseRequested),
        ])
    }
}

async fn measure_download_mbps() -> Result<f64, String> {
    let client = reqwest::Client::builder()
        .timeout(Duration::from_secs(10))
        .build()
        .map_err(|e| e.to_string())?;
    let start = std::time::Instant::now();
    let bytes = client
        .get("https://speed.cloudflare.com/__down?bytes=25000000")
        .send()
        .await
        .map_err(|e| e.to_string())?
        .bytes()
        .await
        .map_err(|e| e.to_string())?;
    let secs = start.elapsed().as_secs_f64().max(0.1);
    Ok(((bytes.len() as f64 * 8.0) / (secs * 1_000_000.0) * 10.0).round() / 10.0)
}

fn trim_memory() {
    unsafe {
        use windows_sys::Win32::System::Memory::SetProcessWorkingSetSizeEx;
        use windows_sys::Win32::System::Threading::GetCurrentProcess;
        SetProcessWorkingSetSizeEx(GetCurrentProcess(), usize::MAX, usize::MAX, 0);
    }
}

pub fn main() -> iced::Result {
    let start_minimized = std::env::args().any(|a| a.eq_ignore_ascii_case("--minimized")) || startup::is_enabled();
    iced::application(
        move || NetPrioApp::new(start_minimized),
        NetPrioApp::update,
        NetPrioApp::view,
    )
    .title("NetPrio")
    .theme(NetPrioApp::theme)
    .subscription(NetPrioApp::subscription)
    .window(iced::window::Settings {
        visible: !start_minimized,
        exit_on_close_request: false,
        ..Default::default()
    })
    .run()
}
