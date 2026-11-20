//! The pages the loopback listener shows in the browser (docs/design DesktopLogin, screen 2). They load
//! nothing else (the listener sends a CSP of `default-src 'none'`), so the styles are inline.

/// Asks the browser to open the app. A placeholder like the product name: it must match `plugins.deep-link` in
/// tauri.conf.json. The address carries nothing, and the app ignores it (contract, flow 1, step 4).
const OPEN_APP_URL: &str = "backroom://open";

/// The sign-in page's language, from the app; anything but Turkish is English (the default locale).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Locale {
    English,
    Turkish,
}

impl Locale {
    pub fn from_tag(tag: &str) -> Self {
        if tag.eq_ignore_ascii_case("tr") || tag.to_ascii_lowercase().starts_with("tr-") {
            Self::Turkish
        } else {
            Self::English
        }
    }

    /// For Keycloak's `ui_locales`.
    pub fn tag(self) -> &'static str {
        match self {
            Self::English => "en",
            Self::Turkish => "tr",
        }
    }
}

pub fn complete(locale: Locale) -> String {
    let (title, text, open_app, hint) = match locale {
        Locale::Turkish => (
            "Giriş tamamlandı",
            "Masaüstü uygulamasına dönebilirsin. Uygulama öne gelmediyse düğmeyi kullan.",
            "Uygulamayı aç",
            "Bu sekmeyi kapatabilirsin.",
        ),
        Locale::English => (
            "Sign-in complete",
            "You can go back to the desktop app. If it didn't come to the front, use the button.",
            "Open the app",
            "You can close this tab.",
        ),
    };
    render(locale, title, text, hint, Some(open_app))
}

pub fn failed(locale: Locale) -> String {
    let (title, text, hint) = match locale {
        Locale::Turkish => (
            "Giriş tamamlanamadı",
            "Masaüstü uygulamasına dön ve yeniden dene.",
            "Bu sekmeyi kapatabilirsin.",
        ),
        Locale::English => (
            "Sign-in didn't finish",
            "Go back to the desktop app and try again.",
            "You can close this tab.",
        ),
    };
    render(locale, title, text, hint, None)
}

/// With `open_app`, the sign-in went through: the page asks the browser to open the app once, without a script
/// (the listener's CSP allows none), and keeps a button for a browser that does not ask or a person who said no.
fn render(locale: Locale, title: &str, text: &str, hint: &str, open_app: Option<&str>) -> String {
    let (icon, icon_style) = if open_app.is_some() {
        ("&#10003;", "background:#e6f4ea;color:#1e6b34")
    } else {
        ("!", "background:#fdecec;color:#9b1c1c")
    };
    let refresh = open_app.map_or(String::new(), |_| {
        format!("<meta http-equiv=\"refresh\" content=\"1;url={OPEN_APP_URL}\">\n")
    });
    let button = open_app.map_or(String::new(), |label| {
        format!("<a class=\"go\" href=\"{OPEN_APP_URL}\">{label}</a>\n")
    });
    format!(
        r#"<!doctype html>
<html lang="{lang}">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta name="referrer" content="no-referrer">
<title>{title}</title>
{refresh}<style>
:root{{color-scheme:light dark;--bg:#f6f7f9;--ink:#1f2430;--mut:#555d6b}}
@media (prefers-color-scheme:dark){{:root{{--bg:#12151a;--ink:#e6e9ef;--mut:#a3abb9}}}}
body{{margin:0;min-height:100vh;display:flex;align-items:center;justify-content:center;background:var(--bg);color:var(--ink);font:14px/1.5 'IBM Plex Sans',system-ui,sans-serif}}
main{{display:flex;flex-direction:column;align-items:center;gap:12px;padding:24px 16px;text-align:center;max-width:420px}}
.icon{{width:44px;height:44px;border-radius:12px;display:flex;align-items:center;justify-content:center;font-size:22px;font-weight:700;{icon_style}}}
h1{{margin:0;font-size:22px}}
p{{margin:0}}
small{{font-size:12px;color:var(--mut)}}
.go{{display:inline-flex;align-items:center;height:44px;padding:0 20px;border-radius:8px;background:#3b5bdb;color:#fff;font-weight:600;text-decoration:none}}
</style>
</head>
<body>
<main>
<div class="icon" aria-hidden="true">{icon}</div>
<h1>{title}</h1>
<p>{text}</p>
{button}<small>{hint}</small>
</main>
</body>
</html>
"#,
        lang = locale.tag()
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn locale_from_tag_is_turkish_only_for_turkish_tags() {
        assert_eq!(Locale::from_tag("tr"), Locale::Turkish);
        assert_eq!(Locale::from_tag("tr-TR"), Locale::Turkish);
        assert_eq!(Locale::from_tag("en-US"), Locale::English);
        assert_eq!(Locale::from_tag("<script>"), Locale::English);
    }

    #[test]
    fn pages_load_nothing_else() {
        for page in [complete(Locale::English), failed(Locale::Turkish)] {
            assert!(!page.contains("src="));
            assert!(!page.contains("<script"));
            assert!(!page.contains("http://") && !page.contains("https://"));
        }
        assert!(complete(Locale::Turkish).contains("Giriş tamamlandı"));
    }

    #[test]
    fn complete_page_asks_to_open_the_app_and_keeps_a_button() {
        let page = complete(Locale::Turkish);

        assert!(page.contains(&format!("content=\"1;url={OPEN_APP_URL}\"")));
        assert!(page.contains(&format!("href=\"{OPEN_APP_URL}\">Uygulamayı aç</a>")));
    }

    #[test]
    fn failed_page_does_not_open_the_app() {
        assert!(!failed(Locale::English).contains(OPEN_APP_URL));
    }

    #[test]
    fn open_app_url_carries_nothing_and_uses_the_configured_scheme() {
        let config: serde_json::Value =
            serde_json::from_str(include_str!("../../tauri.conf.json")).unwrap();
        let scheme = config["plugins"]["deep-link"]["desktop"]["schemes"][0]
            .as_str()
            .unwrap();

        assert_eq!(OPEN_APP_URL, format!("{scheme}://open"));
    }
}
