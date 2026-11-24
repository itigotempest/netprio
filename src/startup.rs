use std::process::Command;

const TASK_NAME: &str = "NetPrioAutoStart";

pub fn is_enabled() -> bool {
    Command::new("schtasks.exe")
        .args(["/Query", "/TN", TASK_NAME])
        .output()
        .map(|o| o.status.success())
        .unwrap_or(false)
}

pub fn set_enabled(enable: bool) -> bool {
    let exe = match std::env::current_exe() {
        Ok(p) => p.to_string_lossy().to_string(),
        Err(_) => return false,
    };

    if enable {
        let tr = format!("\"{}\" --minimized", exe);
        Command::new("schtasks.exe")
            .args(["/Create", "/TN", TASK_NAME, "/TR", &tr, "/SC", "ONLOGON", "/RL", "HIGHEST", "/F"])
            .output()
            .map(|o| o.status.success())
            .unwrap_or(false)
    } else {
        Command::new("schtasks.exe")
            .args(["/Delete", "/TN", TASK_NAME, "/F"])
            .output()
            .map(|o| o.status.success())
            .unwrap_or(false)
    }
}
