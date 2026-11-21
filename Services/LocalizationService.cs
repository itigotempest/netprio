using System.Globalization;

namespace NetPrio.Services;

public static class LocalizationService
{
    public static string ResolveLanguage(string setting)
    {
        if (setting is "ru" or "en") return setting;
        return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("ru", StringComparison.OrdinalIgnoreCase)
            ? "ru"
            : "en";
    }

    private static readonly Dictionary<string, (string Ru, string En)> Strings = new()
    {
        ["AppTitle"] = ("NetPrio — Сетевые приоритеты", "NetPrio — Network Priorities"),
        ["NavPriorities"] = ("Приоритеты", "Priorities"),
        ["NavSettings"] = ("Канал и правила", "Channel & Rules"),
        ["TierHigh"] = ("Наивысший", "Highest"),
        ["TierMed"] = ("Средний", "Medium"),
        ["TierLow"] = ("Минимальный", "Low"),
        ["AddExe"] = ("+ Добавить .exe", "+ Add .exe"),
        ["StartShaper"] = ("Запустить шейпер", "Start Shaper"),
        ["StopShaper"] = ("Остановить шейпер", "Stop Shaper"),
        ["StatusStopped"] = ("Статус шейпера: остановлен", "Shaper status: stopped"),
        ["StatusActive"] = ("Адаптивный шейпер активен", "Adaptive shaper active"),
        ["StatusHint"] = ("Положите WinDivert.dll и WinDivert64.sys рядом с NetPrio.exe и нажмите Запустить", "Place WinDivert.dll and WinDivert64.sys next to NetPrio.exe and click Start"),
        ["AddDialogTitle"] = ("Добавить процесс", "Add Process"),
        ["AddDialogPlaceholder"] = ("Введите или выберите запущенный .exe", "Type or pick a running .exe"),
        ["AddBtn"] = ("Добавить", "Add"),
        ["CancelBtn"] = ("Отмена", "Cancel"),
        ["LangHeader"] = ("Язык интерфейса", "Interface Language"),
        ["LangAuto"] = ("Авто (язык системы)", "Auto (System)"),
        ["AutoSpeedHeader"] = ("Автоматически замерять скорость канала при старте", "Auto-measure channel speed on startup"),
        ["DownloadHeader"] = ("Download (Мбит/с)", "Download (Mbps)"),
        ["UploadHeader"] = ("Upload (Мбит/с)", "Upload (Mbps)"),
        ["MeasureNow"] = ("Замерить сейчас", "Measure Now"),
        ["MedCapHeader"] = ("Лимит для Среднего при активном Наивысшем (%)", "Medium tier cap when Highest is active (%)"),
        ["LowCapHeader"] = ("Лимит для Минимального при активных верхних (%)", "Low tier cap when higher tiers are active (%)"),
        ["ExcludeVpnHeader"] = ("Исключать процесс VPN (TUN-адаптер) из шейпинга", "Exclude VPN process (TUN adapter) from shaping"),
        ["SaveSettings"] = ("Сохранить настройки", "Save Settings"),
        ["Measuring"] = ("Идёт замер скорости канала...", "Measuring channel speed..."),
        ["MeasureDone"] = ("Замер завершён", "Measurement complete"),
        ["MeasureErr"] = ("Ошибка замера", "Measurement error"),
        ["Saved"] = ("Настройки сохранены", "Settings saved"),
        ["Mbps"] = ("Мбит/с", "Mbps"),
        ["NoLimit"] = ("без лимита", "unlimited"),
        ["Limit"] = ("лимит", "limit"),
        ["TrayOpen"] = ("Открыть NetPrio", "Open NetPrio"),
        ["TrayExit"] = ("Выход", "Exit")
    };

    public static string Get(string key, string langSetting)
    {
        var lang = ResolveLanguage(langSetting);
        if (Strings.TryGetValue(key, out var pair))
            return lang == "ru" ? pair.Ru : pair.En;
        return key;
    }
}
