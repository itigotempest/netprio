use std::env;
use std::fs;
use std::path::PathBuf;

fn main() {
    println!("cargo:rustc-check-cfg=cfg(has_windivert_embedded)");
    println!("cargo:rerun-if-changed=WinDivert.dll");
    println!("cargo:rerun-if-changed=WinDivert64.sys");

    let manifest_dir = PathBuf::from(env::var("CARGO_MANIFEST_DIR").unwrap());
    let dll = manifest_dir.join("WinDivert.dll");
    let sys = manifest_dir.join("WinDivert64.sys");

    if dll.exists() && sys.exists() {
        println!("cargo:rustc-cfg=has_windivert_embedded");

        if let Ok(out_dir) = env::var("OUT_DIR") {
            let out_path = PathBuf::from(out_dir);
            if let Some(target_profile_dir) = out_path.ancestors().nth(3) {
                let _ = fs::copy(&dll, target_profile_dir.join("WinDivert.dll"));
                let _ = fs::copy(&sys, target_profile_dir.join("WinDivert64.sys"));
            }
        }
    }

    if env::var("CARGO_CFG_TARGET_OS").as_deref() == Ok("windows") {
        let mut res = winres::WindowsResource::new();
        res.set_icon("assets/app.ico");
        res.set_manifest_file("app.manifest");
        let _ = res.compile();
    }
}
