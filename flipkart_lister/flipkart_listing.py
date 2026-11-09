"""
Flipkart Seller Hub listing assistant (Trusha Enterprise - Roti Cover).

What it does
------------
1. Opens https://seller.flipkart.com/ in a real (visible) Chrome/Chromium window
   using a saved browser profile, so you only log in (OTP/password) once.
2. Tries to open Add Listing -> Single Listing -> vertical -> brand.
   If any navigation step fails, it asks you to do that step by hand and press Enter.
3. Fills the listing form from listing_config.json + description.txt:
   SKU (next in sequence), price/stock/shipping, product description,
   additional description, keywords, and uploads images from ./images.
4. Prints a summary of every field it filled / could not fill.
5. Submits ("Send to QC") ONLY if you type exactly:  send to QC

Usage
-----
    pip install -r requirements.txt
    playwright install chromium        # or use --chrome to use installed Google Chrome
    python flipkart_listing.py                 # normal run
    python flipkart_listing.py --sku-number 4  # force a specific SKU number
    python flipkart_listing.py --dry-run       # open + fill, never submit

Flipkart changes its Seller Hub UI often, so fields are located by their visible
label text instead of fixed CSS ids. Anything the script can't find is listed at
the end so you can fill it by hand in the same window before submitting.
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

from playwright.sync_api import (
    BrowserContext,
    Locator,
    Page,
    TimeoutError as PWTimeout,
    sync_playwright,
)

HERE = Path(__file__).resolve().parent
CONFIG_PATH = HERE / "listing_config.json"
PROFILE_DIR = HERE / "browser_profile"
IMAGES_DIR = HERE / "images"
CONVERTED_DIR = HERE / "_converted"

SHORT = 4_000  # ms
FILLED: list[str] = []
MISSED: list[str] = []


# --------------------------------------------------------------------------- utils
def log(msg: str) -> None:
    print(f"[flipkart] {msg}", flush=True)


def ask(msg: str) -> str:
    return input(f"\n>>> {msg}").strip()


def load_config() -> dict:
    return json.loads(CONFIG_PATH.read_text(encoding="utf-8"))


def save_last_sku_number(n: int) -> None:
    cfg = load_config()
    cfg["last_used_sku_number"] = n
    CONFIG_PATH.write_text(json.dumps(cfg, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


def first_visible(candidates: list[Locator]) -> Locator | None:
    for loc in candidates:
        try:
            n = loc.count()
        except Exception:
            continue
        for i in range(min(n, 5)):
            item = loc.nth(i)
            try:
                if item.is_visible():
                    return item
            except Exception:
                pass
    return None


def click_text(page: Page, *texts: str, timeout: int = SHORT) -> bool:
    """Click the first visible button/link/tab whose text matches one of `texts`."""
    for t in texts:
        rx = re.compile(rf"^\s*{re.escape(t)}\s*$", re.I)
        loc = first_visible([
            page.get_by_role("button", name=rx),
            page.get_by_role("link", name=rx),
            page.get_by_role("tab", name=rx),
            page.get_by_text(rx),
        ])
        if loc:
            try:
                loc.click(timeout=timeout)
                page.wait_for_timeout(800)
                return True
            except Exception:
                continue
    return False


# ------------------------------------------------------------- field location
def find_field(page: Page, label: str) -> Locator | None:
    """Find an input/select/textarea associated with a visible label text."""
    rx = re.compile(rf"^\s*{re.escape(label)}\s*\*?\s*(\(.*\))?\s*:?\s*$", re.I)
    # 1) proper <label for=...> / aria-label / placeholder
    loc = first_visible([
        page.get_by_label(rx),
        page.get_by_placeholder(re.compile(re.escape(label), re.I)),
    ])
    if loc:
        return loc
    # 2) element whose text is the label -> nearest following form control
    label_el = first_visible([page.get_by_text(rx)])
    if not label_el:
        return None
    control_xpath = (
        "xpath=following::*[self::input[not(@type='hidden') and not(@type='file')]"
        " or self::select or self::textarea or @role='combobox'][1]"
    )
    # Prefer a control inside the same row/container first.
    # Walk up a few parents; stop at the first container holding exactly one control,
    # so we never grab a control that belongs to a neighbouring field.
    sel = "input:not([type=hidden]):not([type=file]), select, textarea, [role=combobox]"
    for up in range(1, 4):
        controls = label_el.locator("xpath=" + "/".join([".."] * up)).locator(sel)
        n = controls.count()
        if n == 1:
            return controls.first
        if n > 1:
            break
    ctl = label_el.locator(control_xpath)
    return ctl if ctl.count() else None


def set_value(page: Page, label: str, value: str) -> bool:
    ctl = find_field(page, label)
    if ctl is None:
        MISSED.append(f"{label} = {value}  (field not found)")
        return False
    try:
        ctl.scroll_into_view_if_needed(timeout=SHORT)
        tag = ctl.evaluate("e => e.tagName.toLowerCase()")
        role = (ctl.get_attribute("role") or "").lower()
        if tag == "select":
            try:
                ctl.select_option(label=value, timeout=SHORT)
            except Exception:
                # match option text case-insensitively
                opts = ctl.locator("option").all_inner_texts()
                match = next((o for o in opts if o.strip().lower() == value.lower()), None) \
                    or next((o for o in opts if value.lower() in o.lower()), None)
                if not match:
                    raise
                ctl.select_option(label=match, timeout=SHORT)
        elif role == "combobox" or ctl.get_attribute("readonly") is not None:
            pick_from_dropdown(page, ctl, value)
        else:
            ctl.fill("", timeout=SHORT)
            ctl.fill(value, timeout=SHORT)
            # Some Flipkart inputs are autocompletes: pick a matching suggestion if one appears.
            page.wait_for_timeout(300)
            opt = first_visible([
                page.get_by_role("option", name=re.compile(rf"^\s*{re.escape(value)}\s*$", re.I)),
            ])
            if opt:
                opt.click()
            ctl.press("Tab")
        FILLED.append(f"{label} = {value[:60]}")
        return True
    except Exception as e:
        MISSED.append(f"{label} = {value[:60]}  ({type(e).__name__})")
        return False


def pick_from_dropdown(page: Page, ctl: Locator, value: str) -> None:
    ctl.click(timeout=SHORT)
    try:
        ctl.fill(value, timeout=1500)
    except Exception:
        pass
    page.wait_for_timeout(400)
    rx = re.compile(rf"^\s*{re.escape(value)}\s*$", re.I)
    opt = first_visible([
        page.get_by_role("option", name=rx),
        page.locator("li, [role=option], div[class*=option]").filter(has_text=rx),
    ])
    if not opt:
        raise LookupError(f"option '{value}' not found")
    opt.click(timeout=SHORT)


def add_multi_values(page: Page, label: str, values: list[str]) -> None:
    """Fields like Key Features / Search Keywords: one value per input, with an 'Add' (+) button."""
    ctl = find_field(page, label)
    if ctl is None:
        MISSED.append(f"{label} (field not found) -> {', '.join(values)}")
        return
    added = 0
    for v in values:
        try:
            ctl.fill(v, timeout=SHORT)
            ctl.press("Enter")
            page.wait_for_timeout(250)
            # If Enter didn't create a chip/new row, try an Add / + button next to it.
            if ctl.input_value() == v:
                btn = first_visible([
                    ctl.locator("xpath=following::button[1]"),
                ])
                if btn:
                    btn.click()
                    page.wait_for_timeout(250)
                    nxt = find_field(page, label)
                    if nxt is not None:
                        ctl = nxt.last if nxt.count() > 1 else nxt
            added += 1
        except Exception:
            MISSED.append(f"{label}: '{v}'")
    FILLED.append(f"{label}: {added}/{len(values)} values")


# ------------------------------------------------------------------ images
def prepare_images() -> list[Path]:
    files = sorted(p for p in IMAGES_DIR.iterdir()
                   if p.suffix.lower() in {".jpg", ".jpeg", ".png", ".webp"})[:5]
    out: list[Path] = []
    for p in files:
        if p.suffix.lower() == ".webp":  # Flipkart only accepts JPG/PNG
            from PIL import Image
            CONVERTED_DIR.mkdir(exist_ok=True)
            dst = CONVERTED_DIR / (p.stem + ".jpg")
            Image.open(p).convert("RGB").save(dst, "JPEG", quality=95)
            out.append(dst)
        else:
            out.append(p)
    return out


def upload_images(page: Page) -> None:
    imgs = prepare_images()
    if not imgs:
        MISSED.append("Images: ./images folder is empty - upload by hand")
        return
    inputs = page.locator("input[type=file]")
    if inputs.count() == 0:
        click_text(page, "Upload Photos", "Add Images", "Images")
        page.wait_for_timeout(1000)
    if inputs.count() == 0:
        MISSED.append("Images: no upload field found - upload by hand")
        return
    try:
        if inputs.count() >= len(imgs):          # one input per image slot
            for i, img in enumerate(imgs):
                inputs.nth(i).set_input_files(str(img))
                page.wait_for_timeout(1500)
        else:                                     # one multi-file input
            inputs.first.set_input_files([str(i) for i in imgs])
            page.wait_for_timeout(3000)
        FILLED.append(f"Images: {', '.join(i.name for i in imgs)}")
    except Exception as e:
        MISSED.append(f"Images upload failed ({e.__class__.__name__}) - upload by hand")


# ------------------------------------------------------------------ flow
def wait_for_login(page: Page) -> None:
    page.goto("https://seller.flipkart.com/", wait_until="domcontentloaded")
    page.wait_for_timeout(3000)
    if "login" in page.url.lower() or page.get_by_text(re.compile("^\\s*Login\\s*$", re.I)).count():
        log("Please log in to Seller Hub in the opened browser window (OTP / password).")
    ask("When you can see your Seller Hub dashboard, press Enter... ")


def open_new_listing(page: Page, cfg: dict) -> None:
    log("Opening Add Listing -> Single Listing ...")
    ok = click_text(page, "Listings") and click_text(page, "Add New Listing", "Add Listing", "Add Listings") \
        and click_text(page, "Add Single Listing", "Single Listing", "Add Single")
    if ok:
        search = first_visible([
            page.get_by_placeholder(re.compile("search|vertical|category", re.I)),
            page.locator("input[type=search], input[type=text]"),
        ])
        if search:
            search.fill(cfg["vertical"])
            page.wait_for_timeout(1500)
            ok = click_text(page, cfg["vertical"]) and click_text(page, "Select", "Next", "Continue")
        else:
            ok = False
    if ok:
        brand = first_visible([page.get_by_placeholder(re.compile("brand", re.I))])
        if brand:
            brand.fill(cfg["brand"])
            page.wait_for_timeout(1500)
            ok = click_text(page, cfg["brand"]) and click_text(page, "Check Brand", "Next", "Continue", "Create Listing")
        else:
            ok = False
    if not ok:
        log("Automatic navigation didn't finish (Seller Hub layout differs).")
        log(f"Please open: Listings -> Add New Listing -> Single listing -> '{cfg['vertical']}' "
            f"-> brand '{cfg['brand']}' (or use 'Copy Recently Created Listing').")
    ask("When the listing form (with SKU / Price / Description tabs) is open, press Enter... ")


def fill_section(page: Page, tab_names: tuple[str, ...], fields: dict, sku_number: int) -> None:
    if not click_text(page, *tab_names):
        log(f"Could not open tab {tab_names[0]!r}; filling whatever is visible.")
    for label, value in fields.items():
        if isinstance(value, list):
            add_multi_values(page, label, value)
        else:
            set_value(page, label, str(value).replace("{sku_number}", f"P{sku_number}"))


def fill_listing(page: Page, cfg: dict, sku_number: int) -> str:
    sku = f"{cfg['sku_prefix']}{sku_number}"
    log(f"Filling SKU: {sku}")
    set_value(page, "Seller SKU ID", sku) or set_value(page, "SKU", sku)

    upload_images(page)

    fill_section(page, ("Price, Stock and Shipping Information", "Price, Stock and Shipping", "Price"),
                 cfg["price_stock_shipping"], sku_number)

    fill_section(page, ("Product Description",), cfg["product_description"], sku_number)

    add = dict(cfg["additional_description"])
    desc_file = add.pop("Description")
    fill_section(page, ("Additional Description (Optional)", "Additional Description"), add, sku_number)
    set_value(page, "Description", (HERE / desc_file).read_text(encoding="utf-8").strip())
    return sku


def summary(sku: str) -> None:
    print("\n" + "=" * 70)
    print(f" LISTING SUMMARY   SKU: {sku}")
    print("=" * 70)
    print(f" Filled ({len(FILLED)}):")
    for f in FILLED:
        print("   ✔", f)
    if MISSED:
        print(f"\n Needs your attention ({len(MISSED)}) - please fill these in the browser:")
        for m in MISSED:
            print("   ✘", m)
    print("=" * 70)


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--sku-number", type=int, help="SKU number to use (default: last used + 1)")
    ap.add_argument("--chrome", action="store_true", help="use installed Google Chrome instead of bundled Chromium")
    ap.add_argument("--dry-run", action="store_true", help="never submit, even if asked")
    args = ap.parse_args()

    cfg = load_config()
    sku_number = args.sku_number or cfg["last_used_sku_number"] + 1
    answer = ask(f"Last used SKU was '{cfg['sku_prefix']}{cfg['last_used_sku_number']}'. "
                 f"Use '{cfg['sku_prefix']}{sku_number}' for this listing? [Y / or type a number]: ")
    if answer.isdigit():
        sku_number = int(answer)
    elif answer.lower() not in ("", "y", "yes"):
        sys.exit("Cancelled.")

    if not any(IMAGES_DIR.glob("*.*")) or all(p.name == "README.txt" for p in IMAGES_DIR.iterdir()):
        log("No images in ./images - you'll need to upload them by hand.")
    log("Reminder: don't use photos carrying another brand's logo/watermark.")

    with sync_playwright() as pw:
        ctx: BrowserContext = pw.chromium.launch_persistent_context(
            str(PROFILE_DIR),
            headless=False,
            channel="chrome" if args.chrome else None,
            viewport={"width": 1366, "height": 900},
            args=["--start-maximized"],
        )
        page = ctx.pages[0] if ctx.pages else ctx.new_page()
        page.set_default_timeout(15_000)

        wait_for_login(page)
        open_new_listing(page, cfg)
        sku = fill_listing(page, cfg, sku_number)
        summary(sku)

        log("Flipkart auto-saves the draft. Review the form in the browser and fix anything marked ✘.")
        if args.dry_run:
            ask("Dry run - nothing will be submitted. Press Enter to close the browser... ")
        else:
            confirm = ask("Type exactly 'send to QC' to submit, or just press Enter to leave it as a draft: ")
            if confirm.lower() == "send to qc":
                if click_text(page, "Send to QC", "Send To QC", "Submit"):
                    page.wait_for_timeout(4000)
                    log(f"Submitted {sku} to QC.")
                    save_last_sku_number(sku_number)
                else:
                    log("Couldn't find the 'Send to QC' button - please click it yourself.")
                    if ask("Did you submit it? [y/N]: ").lower() == "y":
                        save_last_sku_number(sku_number)
            else:
                log("Left as draft (Listings -> Listings in Progress).")
            ask("Press Enter to close the browser... ")
        ctx.close()


if __name__ == "__main__":
    try:
        main()
    except (KeyboardInterrupt, EOFError):
        print("\nStopped.")
    except PWTimeout as e:
        sys.exit(f"Timed out: {e}")
