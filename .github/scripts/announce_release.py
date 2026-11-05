#!/usr/bin/env python3
"""Announces a release: Telegram (and MAX) in Russian, Discord in English.

The posts are the release's sections of CHANGELOG.ru.md and CHANGELOG.md. A release without a
Russian section gets a short Russian post with the links only.

Each channel is posted to only when its secrets are set, and a failed channel doesn't stop
the others. The exit code is nonzero when any configured channel failed.

Usage: announce_release.py <tag> [--dry-run]

Environment (repository secrets):
  GITHUB_REPOSITORY                       owner/name, for the release link
  TELEGRAM_BOT_TOKEN                      Telegram bot, an admin of the channel
  TELEGRAM_CHAT_ID                        the Telegram channel, @chamelab when not set
  MAX_BOT_TOKEN, MAX_CHAT_ID              MAX channel (numeric id); the bot is its admin
  DISCORD_WEBHOOK_URL                     Discord channel webhook

Try it locally without posting: announce_release.py v1.3.33 --dry-run
"""

import html
import json
import os
import re
import sys
import urllib.error
import urllib.request
from pathlib import Path

SITE_URL = "https://m0n5ter.github.io/ChameLab/#stable"
TELEGRAM_CHANNEL = "@chamelab"

# Message size limits, with room to spare: Discord 2000, Telegram 4096, MAX 4000
DISCORD_LIMIT = 1900
TELEGRAM_LIMIT = 4000
MAX_LIMIT = 3900


def changelog_section(path: str, tag: str) -> tuple[str | None, list[str]] | None:
    """The firmware version and the bullets of the tag's section, None without a section."""
    lines = Path(path).read_text(encoding="utf-8").splitlines()
    heading = re.compile(r"^## \[" + re.escape(tag) + r"\]")
    start = next((i for i, line in enumerate(lines) if heading.match(line)), None)
    if start is None:
        return None
    firmware = re.search(r"(?:firmware|прошивка) (v\S+)", lines[start])
    bullets: list[str] = []
    for line in lines[start + 1 :]:
        if line.startswith("## "):
            break
        stripped = line.strip()
        if stripped.startswith("- "):
            bullets.append(stripped[2:].strip())
        elif stripped and bullets:
            # A bullet continued on the next line
            bullets[-1] += " " + stripped
    return (firmware.group(1) if firmware else None), bullets


def build_post(title: str, bullets: list[str], more: str, links: str, limit: int, render) -> str:
    """The post with as many whole bullets as fit into the limit once rendered."""
    for count in range(len(bullets), -1, -1):
        items = [f"- {bullet}" for bullet in bullets[:count]]
        if count < len(bullets):
            items.append(more)
        sections = [f"**{title}**", "\n".join(items), links]
        text = render("\n\n".join(section for section in sections if section))
        if len(text) <= limit:
            return text
    return render(f"**{title}**\n\n{links}")


def markdown_to_html(text: str) -> str:
    """The post's Markdown (**bold**, `code`, "- " bullets) as Telegram / MAX HTML."""
    out = []
    for line in text.split("\n"):
        line = html.escape(line, quote=False)
        line = re.sub(r"`([^`]+)`", r"<code>\1</code>", line)
        line = re.sub(r"\*\*(.+?)\*\*", r"<b>\1</b>", line)
        if line.startswith("- "):
            line = "• " + line[2:]
        out.append(line)
    return "\n".join(out)


def request(url: str, body: dict, headers: dict | None = None, timeout: int = 60) -> dict:
    data = json.dumps(body).encode()
    req = urllib.request.Request(url, data=data, method="POST")
    req.add_header("Content-Type", "application/json")
    # Discord's front end turns away the default Python-urllib agent
    req.add_header("User-Agent", "ChameLab-release-announcer")
    for name, value in (headers or {}).items():
        req.add_header(name, value)
    try:
        with urllib.request.urlopen(req, timeout=timeout) as response:
            payload = response.read()
    except urllib.error.HTTPError as e:
        raise RuntimeError(f"HTTP {e.code}: {e.read().decode(errors='replace')[:500]}") from e
    return json.loads(payload) if payload else {}


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8")  # type: ignore[union-attr]
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    dry_run = "--dry-run" in sys.argv
    if len(args) != 1:
        raise SystemExit(__doc__)
    tag = args[0]
    repo = os.environ.get("GITHUB_REPOSITORY", "m0n5ter/ChameLab")
    release_url = f"https://github.com/{repo}/releases/tag/{tag}"

    english = changelog_section("CHANGELOG.md", tag)
    if english is None:
        raise SystemExit(f"CHANGELOG.md has no section for {tag}")
    firmware, bullets = english
    russian = changelog_section("CHANGELOG.ru.md", tag)
    if russian is None:
        print(f"::warning::CHANGELOG.ru.md has no section for {tag}; the Russian posts carry the links only")
    ru_bullets = russian[1] if russian else []

    en_title = f"ChameLab {tag} is out" + (f" · firmware {firmware}" if firmware else "")
    en_more = "- …and more in the release notes"
    en_links = f"Download: {SITE_URL}\nRelease notes: {release_url}"

    ru_title = f"Вышел ChameLab {tag}" + (f" · прошивка {firmware}" if firmware else "")
    ru_more = "- …и другие изменения в описании релиза"
    ru_links = f"Скачать: {SITE_URL}\nОписание релиза: {release_url}"

    def identity(text: str) -> str:
        return text

    posts = {
        "discord": build_post(en_title, bullets, en_more, en_links, DISCORD_LIMIT, identity),
        "telegram": build_post(ru_title, ru_bullets, ru_more, ru_links, TELEGRAM_LIMIT, markdown_to_html),
        "max": build_post(ru_title, ru_bullets, ru_more, ru_links, MAX_LIMIT, markdown_to_html),
    }

    if dry_run:
        for channel, text in posts.items():
            print(f"===== {channel} ({len(text)} chars) =====\n{text}\n")
        return 0

    failed = False

    def post(channel: str, configured: bool, send) -> None:
        nonlocal failed
        if not configured:
            print(f"{channel}: not configured, skipped")
            return
        try:
            send(posts[channel])
            print(f"{channel}: posted")
        except Exception as e:  # noqa: BLE001 - report and carry on with the other channels
            failed = True
            print(f"::error::{channel}: {e}")

    env = os.environ.get
    post(
        "telegram",
        bool(env("TELEGRAM_BOT_TOKEN")),
        lambda text: request(
            f"https://api.telegram.org/bot{env('TELEGRAM_BOT_TOKEN')}/sendMessage",
            {
                "chat_id": env("TELEGRAM_CHAT_ID") or TELEGRAM_CHANNEL,
                "text": text,
                "parse_mode": "HTML",
                "link_preview_options": {"is_disabled": True},
            },
        ),
    )
    post(
        "max",
        bool(env("MAX_BOT_TOKEN") and env("MAX_CHAT_ID")),
        lambda text: request(
            f"https://platform-api2.max.ru/messages?chat_id={env('MAX_CHAT_ID')}&disable_link_preview=true",
            {"text": text, "format": "html"},
            {"Authorization": env("MAX_BOT_TOKEN") or ""},
        ),
    )
    post(
        "discord",
        bool(env("DISCORD_WEBHOOK_URL")),
        lambda text: request(
            env("DISCORD_WEBHOOK_URL") or "",
            # Links stay links, without embeds; no @everyone or role pings from the text
            {"content": text, "flags": 4, "allowed_mentions": {"parse": []}},
        ),
    )
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
