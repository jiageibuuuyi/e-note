import os
from PIL import Image, ImageDraw, ImageFont, ImageFilter

def create_banner():
    banner_w, banner_h = 1240, 580
    banner = Image.new("RGBA", (banner_w, banner_h), (248, 249, 251, 255))
    draw = ImageDraw.Draw(banner)

    # Subtle modern dot grid pattern for tech feel
    for x in range(30, banner_w, 24):
        for y in range(30, banner_h, 24):
            draw.ellipse((x, y, x + 2, y + 2), fill=(225, 228, 233, 140))

    # Font setup
    fonts_dir = os.path.join(os.environ.get("WINDIR", "C:\\Windows"), "Fonts")
    font_bold = os.path.join(fonts_dir, "msyhbd.ttc")
    font_regular = os.path.join(fonts_dir, "msyh.ttc")

    title_font = ImageFont.truetype(font_bold, 36)
    sub_font = ImageFont.truetype(font_bold, 18)
    desc_font = ImageFont.truetype(font_regular, 14)
    badge_title_font = ImageFont.truetype(font_bold, 13)
    badge_font = ImageFont.truetype(font_regular, 13)

    # Left content: Hero branding
    icon_path = os.path.join("docs", "images", "app-icon.png")
    if os.path.exists(icon_path):
        icon = Image.open(icon_path).convert("RGBA")
        icon = icon.resize((72, 72), Image.Resampling.LANCZOS)
        banner.paste(icon, (60, 48), icon)

    # App Title & Slogan
    draw.text((144, 48), "e-note", font=title_font, fill=(30, 32, 38))
    draw.text((276, 56), "· 电子便签", font=ImageFont.truetype(font_regular, 26), fill=(100, 105, 115))
    draw.text((60, 134), "桌面上的温暖与秩序", font=sub_font, fill=(45, 50, 60))
    draw.text((60, 164), "跨平台高质感桌面便签、待办管理与 AI Agent 工作台", font=desc_font, fill=(110, 115, 125))

    # Feature Badges
    badges = [
        ("● 写实拟物风格", "微光曲面纸张与立体图钉美学", (246, 240, 216), (145, 100, 20)),
        ("● 现代极简模式", "极简清爽 Fluent 卡片无缝切换", (236, 243, 252), (40, 95, 175)),
        ("● 黄金比例月历", "1:1.1 匀称方框与全周期事项追踪", (236, 246, 238), (35, 120, 55)),
        ("● MCP Agent 接入", "标准 JSON-RPC 本地安全事务直连", (247, 236, 250), (135, 45, 150)),
        ("● 纯本地离线存储", "SQLite 并发事务隔离 · 零云端泄露", (240, 242, 245), (70, 75, 85)),
    ]

    badge_y = 208
    for label, desc, bg_color, fg_color in badges:
        # Draw badge pill
        draw.rounded_rectangle((60, badge_y, 455, badge_y + 44), radius=8, fill=bg_color)
        draw.text((75, badge_y + 12), label, font=badge_title_font, fill=fg_color)
        draw.text((205, badge_y + 12), f"·  {desc}", font=badge_font, fill=(90, 95, 105))
        badge_y += 56

    # Platform tag footer
    platform_tags = ["Windows x64", "macOS (Apple Silicon / Intel)", "Linux (deb / tar.gz)"]
    plat_x = 60
    plat_y = 498
    for tag in platform_tags:
        bbox = draw.textbbox((0, 0), tag, font=badge_font)
        tw = bbox[2] - bbox[0]
        draw.rounded_rectangle((plat_x, plat_y, plat_x + tw + 20, plat_y + 26), radius=13, fill=(235, 238, 242))
        draw.text((plat_x + 10, plat_y + 4), tag, font=badge_font, fill=(100, 105, 115))
        plat_x += tw + 30

    # Right section: Display real screenshots with soft dropshadows
    real_path = os.path.join("docs", "images", "screenshot-realistic.png")
    mini_path = os.path.join("docs", "images", "screenshot-minimalist.png")

    target_h = 490

    if os.path.exists(real_path) and os.path.exists(mini_path):
        img_real = Image.open(real_path).convert("RGBA")
        img_mini = Image.open(mini_path).convert("RGBA")

        # Scale maintaining aspect ratio
        ratio_real = target_h / img_real.height
        w_real = int(img_real.width * ratio_real)
        img_real = img_real.resize((w_real, target_h), Image.Resampling.LANCZOS)

        ratio_mini = target_h / img_mini.height
        w_mini = int(img_mini.width * ratio_mini)
        img_mini = img_mini.resize((w_mini, target_h), Image.Resampling.LANCZOS)

        # Create soft drop shadow function
        def paste_with_shadow(base, img, pos_x, pos_y):
            shadow = Image.new("RGBA", (img.width + 40, img.height + 40), (0, 0, 0, 0))
            s_draw = ImageDraw.Draw(shadow)
            s_draw.rectangle((20, 20, img.width + 20, img.height + 20), fill=(0, 0, 0, 45))
            shadow = shadow.filter(ImageFilter.GaussianBlur(15))
            base.paste(shadow, (pos_x - 20, pos_y - 10), shadow)
            base.paste(img, (pos_x, pos_y), img)

        paste_with_shadow(banner, img_mini, 875, 45)
        paste_with_shadow(banner, img_real, 510, 45)

    # Save final banner
    out_path = os.path.join("docs", "images", "banner.png")
    banner.convert("RGB").save(out_path, quality=95)
    print(f"Successfully created {out_path} ({banner_w}x{banner_h})")

if __name__ == "__main__":
    create_banner()
