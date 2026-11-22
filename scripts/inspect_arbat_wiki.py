#!/usr/bin/env python3
"""Fetch Arbat legal pages and dump their HTML/DOM structure for a safe importer."""
from __future__ import annotations
import json
import re
import time
from pathlib import Path
from urllib.request import Request, urlopen
from urllib.error import URLError, HTTPError
from bs4 import BeautifulSoup

PAGES = [
    ("criminal-code", "arbat-ugolovnyy-kodeks"),
    ("constitution", "arbat-konstituciya"),
    ("criminal-procedure", "arbat-ugolovno-processualnyy-kodeks"),
    ("koap", "arbat-koap"),
    ("labor", "arbat-trudovoy-kodeks"),
    ("ethics", "arbat-kodeks-etiki"),
    ("court", "arbat-fkz-federalnyy-konstitucionnyy-zakon-o-sudebnoy-sisteme-ro"),
    ("gibdd-disciplinary", "arbat-disciplinarnyy-ustav-gibdd"),
    ("gibdd-charter", "arbat-ustav-gibdd"),
    ("government", "arbat-fkz-o-pravitelstve-ro"),
    ("traffic", "arbat-fz-pravila-dorozhnogo-dvizheniya-ro"),
    ("police", "arbat-fz-o-policii"),
    ("prosecutor", "arbat-fz-o-generalnoy-prokurature-ro"),
    ("security-service", "arbat-fz-o-federalnoy-sluzhbe-bezopasnosti"),
    ("emergency", "arbat-fkz-o-chrezvychaynom-i-voennom-polozhenii"),
    ("territories", "arbat-fz-o-statuse-territoriy-i-osobyh-obektah-ro"),
    ("weapons", "arbat-fz-ob-oborote-oruzhiya-i-gosudarstvennyh-specialnyh-sredstv-ro"),
    ("fso", "arbat-fz-federalnoy-sluzhbe-ohrany-ro"),
    ("documents", "arbat-fz-o-gosudarstvennyh-dokumentah"),
    ("armed-forces", "arbat-fz-o-vooruzhennyh-silah-ro"),
    ("business", "arbat-fz-o-predprinimatelskoy-deyatelnosti"),
    ("investigative-committee", "arbat-fz-o-sledstvennom-komitete-ro"),
    ("advocacy", "arbat-fz-ob-advokatskoy-deyatelnosti-i-advokature-v-ro"),
    ("media", "arbat-fz-o-sredstvah-massovoy-informacii"),
    ("immunity", "arbat-fz-o-statuse-neprikosnovennosti-dolzhnostnyh-lic"),
    ("medical", "arbat-fz-o-statuse-meditsinskih-uchrezhdeniy"),
    ("internal-security", "arbat-fz-polozhenie-o-rabote-otdela-sobstvennoy-bezopasnosti"),
]

OUT = Path("arbat-wiki-inspection")
RAW = OUT / "raw"
RAW.mkdir(parents=True, exist_ok=True)
report = []

for key, slug in PAGES:
    url = f"https://russiaonline.wiki/wiki/laws/{slug}"
    req = Request(url, headers={
        "User-Agent": "Mozilla/5.0 (compatible; RussiaOnlineStudio/1.0; legal catalogue import)",
        "Accept": "text/html,application/xhtml+xml",
    })
    try:
        with urlopen(req, timeout=35) as response:
            raw = response.read()
            charset = response.headers.get_content_charset() or "utf-8"
            html = raw.decode(charset, errors="replace")
            status = response.status
    except (HTTPError, URLError, TimeoutError) as exc:
        report.append({"key": key, "slug": slug, "url": url, "error": str(exc)})
        continue

    (RAW / f"{key}.html").write_text(html, encoding="utf-8")
    soup = BeautifulSoup(html, "html.parser")
    h1 = soup.find("h1")
    title = h1.get_text(" ", strip=True) if h1 else (soup.title.get_text(" ", strip=True) if soup.title else key)
    body = soup.body or soup
    headings = []
    for el in body.find_all(["h1", "h2", "h3", "h4", "h5"]):
        headings.append({
            "tag": el.name,
            "text": el.get_text(" ", strip=True),
            "id": el.get("id"),
            "class": el.get("class"),
            "parent": el.parent.name if el.parent else None,
            "parent_id": el.parent.get("id") if el.parent else None,
            "parent_class": el.parent.get("class") if el.parent else None,
        })
    ids = []
    for el in body.find_all(attrs={"id": True}):
        value = el.get("id", "")
        if re.search(r"(article|law|section|content|chapter|post)", value, re.I):
            ids.append({"tag": el.name, "id": value, "class": el.get("class"), "text": el.get_text(" ", strip=True)[:180]})
    text_body = body.get_text("\\n", strip=True)
    report.append({
        "key": key, "slug": slug, "url": url, "status": status, "bytes": len(raw),
        "title": title, "text_chars": len(text_body), "headings": headings, "candidate_ids": ids,
        "text_preview": text_body[:1600],
    })
    print(f"{key}: HTTP {status}, {len(raw)} bytes, {len(headings)} headings, {len(ids)} candidate IDs")
    time.sleep(0.3)

(OUT / "report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
print(f"Fetched {sum(1 for x in report if 'error' not in x)} / {len(PAGES)} pages")
