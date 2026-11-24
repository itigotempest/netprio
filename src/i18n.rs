use windows_sys::Win32::Globalization::GetUserDefaultUILanguage;

pub fn resolve_lang(setting: &str) -> &'static str {
    match setting {
        "ru" => "ru",
        "en" => "en",
        _ => {
            let lang_id = unsafe { GetUserDefaultUILanguage() };
            // Primary language ID 0x19 = Russian
            if (lang_id & 0x03FF) == 0x19 { "ru" } else { "en" }
        }
    }
}

pub fn t(key: &str, setting: &str) -> &'static str {
    let is_ru = resolve_lang(setting) == "ru";
    match (key, is_ru) {
        ("title", true) => "NetPrio — Сетевые приоритеты",
        ("title", false) => "NetPrio — Network Priorities",
        ("tab_priorities", true) => "Приоритеты",
        ("tab_priorities", false) => "Priorities",
        ("tab_settings", true) => "Канал и правила",
        ("tab_settings", false) => "Channel & Rules",
        ("high", true) => "Наивысший",
        ("high", false) => "Highest",
        ("med", true) => "Средний",
        ("med", false) => "Medium",
        ("low", true) => "Минимальный",
        ("low", false) => "Low",
        ("start", true) => "Запустить шейпер",
        ("start", false) => "Start Shaper",
        ("stop", true) => "Остановить шейпер",
        ("stop", false) => "Stop Shaper",
        ("status_active", true) => "Адаптивный шейпер активен",
        ("status_active", false) => "Adaptive shaper active",
        ("status_stopped", true) => "Шейпер остановлен",
        ("status_stopped", false) => "Shaper stopped",
        ("add_placeholder", true) => "process.exe...",
        ("add_placeholder", false) => "process.exe...",
        ("add_btn", true) => "+ Добавить",
        ("add_btn", false) => "+ Add",
        ("lang", true) => "Язык интерфейса",
        ("lang", false) => "Interface Language",
        ("autostart", true) => "Запускать с Windows в трее (без UAC)",
        ("autostart", false) => "Start minimized to tray with Windows (no UAC)",
        ("close_tray", true) => "Сворачивать в трей по крестику (✕)",
        ("close_tray", false) => "Minimize to tray on close (✕)",
        ("download_mbps", true) => "Download (Мбит/с)",
        ("download_mbps", false) => "Download (Mbps)",
        ("measure", true) => "Замерить сейчас",
        ("measure", false) => "Measure Now",
        ("med_cap", true) => "Лимит Среднего при активном Наивысшем (%)",
        ("med_cap", false) => "Medium tier cap when Highest is active (%)",
        ("low_cap", true) => "Лимит Минимального при активных верхних (%)",
        ("low_cap", false) => "Low tier cap when higher tiers are active (%)",
        ("skip_csum", true) => "Отключить пересчёт чексумм (экспериментально)",
        ("skip_csum", false) => "Skip checksum recalculation (experimental)",
        ("save", true) => "Сохранить настройки",
        ("save", false) => "Save Settings",
        ("mbps", true) => "Мбит/с",
        ("mbps", false) => "Mbps",
        ("unlimited", true) => "без лимита",
        ("unlimited", false) => "unlimited",
        ("limit", true) => "лимит",
        ("limit", false) => "limit",
        ("tray_open", true) => "Открыть NetPrio",
        ("tray_open", false) => "Open NetPrio",
        ("tray_exit", true) => "Выход",
        ("tray_exit", false) => "Exit",
        _ => "",
    }
}
