use serde::{Deserialize, Serialize};
use std::fs;
use std::path::PathBuf;

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct PriorityConfig {
    #[serde(default)]
    pub highest_apps: Vec<String>,
    #[serde(default)]
    pub medium_apps: Vec<String>,
    #[serde(default)]
    pub low_apps: Vec<String>,
    #[serde(default = "default_lang")]
    pub language: String,
    #[serde(default = "default_true")]
    pub close_to_tray: bool,
    #[serde(default = "default_true")]
    pub auto_measure_speed: bool,
    #[serde(default = "default_speed")]
    pub channel_download_mbps: f64,
    #[serde(default = "default_speed")]
    pub channel_upload_mbps: f64,
    #[serde(default = "default_med_cap")]
    pub medium_cap_percent: u32,
    #[serde(default = "default_low_cap")]
    pub low_cap_percent: u32,
    #[serde(default = "default_floor")]
    pub minimum_floor_mbps: f64,
    #[serde(default = "default_true")]
    pub exclude_vpn_tunnel_process: bool,
    #[serde(default)]
    pub skip_checksum_recalc: bool,
}

fn default_lang() -> String { "auto".to_string() }
fn default_true() -> bool { true }
fn default_speed() -> f64 { 100.0 }
fn default_med_cap() -> u32 { 50 }
fn default_low_cap() -> u32 { 10 }
fn default_floor() -> f64 { 2.0 }

impl Default for PriorityConfig {
    fn default() -> Self {
        Self {
            highest_apps: Vec::new(),
            medium_apps: Vec::new(),
            low_apps: Vec::new(),
            language: default_lang(),
            close_to_tray: true,
            auto_measure_speed: true,
            channel_download_mbps: 100.0,
            channel_upload_mbps: 100.0,
            medium_cap_percent: 50,
            low_cap_percent: 10,
            minimum_floor_mbps: 2.0,
            exclude_vpn_tunnel_process: true,
            skip_checksum_recalc: false,
        }
    }
}

impl PriorityConfig {
    pub fn config_path() -> PathBuf {
        let base = std::env::var_os("APPDATA")
            .map(PathBuf::from)
            .unwrap_or_else(|| PathBuf::from("."));
        base.join("NetPrio").join("config.json")
    }

    pub fn load() -> Self {
        let path = Self::config_path();
        if let Ok(raw) = fs::read_to_string(&path) {
            if let Ok(cfg) = serde_json::from_str::<Self>(&raw) {
                return cfg;
            }
        }
        Self::default()
    }

    pub fn save(&self) {
        let path = Self::config_path();
        if let Some(parent) = path.parent() {
            let _ = fs::create_dir_all(parent);
        }
        if let Ok(json) = serde_json::to_string_pretty(self) {
            let _ = fs::write(path, json);
        }
    }
}
