#!/usr/bin/env python3
"""Import the 27 legal documents listed for Russia Online's Arbat server."""
import json, re, time, sys

if hasattr(sys.stdout, 'reconfigure'):
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
from pathlib import Path
from urllib.request import Request, urlopen
from urllib.error import HTTPError, URLError
from bs4 import BeautifulSoup, NavigableString, Tag

ROOT = Path.cwd()
DATA = ROOT / "Data"
EXT = DATA / "ExtendedLaws"
OUT = EXT
PAGES = [
 (1,"criminal-code","arbat-ugolovnyy-kodeks","УК","criminal-code.json"),
 (2,"constitution","arbat-konstituciya","Конституция","constitution.json"),
 (3,"criminal-procedure","arbat-ugolovno-processualnyy-kodeks","УПК","criminal-procedure.json"),
 (4,"koap","arbat-koap","КоАП","koap.json"),
 (5,"labor","arbat-trudovoy-kodeks","ТК","labor.json"),
 (6,"ethics","arbat-kodeks-etiki","Кодекс этики","ethics.json"),
 (7,"court","arbat-fkz-federalnyy-konstitucionnyy-zakon-o-sudebnoy-sisteme-ro","Судебная система","court.json"),
 (8,"gibdd-disciplinary","arbat-disciplinarnyy-ustav-gibdd","Дисциплинарный устав ГИБДД","gibdd-disciplinary.json"),
 (9,"gibdd-charter","arbat-ustav-gibdd","Внутренний устав ГИБДД","gibdd-charter.json"),
 (10,"government","arbat-fkz-o-pravitelstve-ro","Правительство РО","government-law.json"),
 (11,"traffic","arbat-fz-pravila-dorozhnogo-dvizheniya-ro","ПДД","traffic.json"),
 (12,"police","arbat-fz-o-policii","Полиция","police.json"),
 (13,"prosecutor","arbat-fz-o-generalnoy-prokurature-ro","Генеральная прокуратура","prosecutor.json"),
 (14,"security-service","arbat-fz-o-federalnoy-sluzhbe-bezopasnosti","ФСБ","security-service.json"),
 (15,"emergency","arbat-fkz-o-chrezvychaynom-i-voennom-polozhenii","Чрезвычайное и военное положение","emergency.json"),
 (16,"territories","arbat-fz-o-statuse-territoriy-i-osobyh-obektah-ro","Статус территорий","territories.json"),
 (17,"weapons","arbat-fz-ob-oborote-oruzhiya-i-gosudarstvennyh-specialnyh-sredstv-ro","Оборот оружия","weapons.json"),
 (18,"fso","arbat-fz-federalnoy-sluzhbe-ohrany-ro","ФСО","fso.json"),
 (19,"documents","arbat-fz-o-gosudarstvennyh-dokumentah","Государственные документы","documents.json"),
 (20,"armed-forces","arbat-fz-o-vooruzhennyh-silah-ro","Вооружённые Силы РО","armed-forces.json"),
 (21,"business","arbat-fz-o-predprinimatelskoy-deyatelnosti","Предпринимательство","business.json"),
 (22,"investigative-committee","arbat-fz-o-sledstvennom-komitete-ro","Следственный комитет","investigative-committee.json"),
 (23,"advocacy","arbat-fz-ob-advokatskoy-deyatelnosti-i-advokature-v-ro","Адвокатура","advocacy.json"),
 (24,"media","arbat-fz-o-sredstvah-massovoy-informacii","СМИ","media.json"),
 (25,"immunity","arbat-fz-o-statuse-neprikosnovennosti-dolzhnostnyh-lic","Неприкосновенность должностных лиц","immunity.json"),
 (26,"medical","arbat-fz-o-statuse-meditsinskih-uchrezhdeniy","Медицинские учреждения","medical.json"),
 (27,"internal-security","arbat-fz-polozhenie-o-rabote-otdela-sobstvennoy-bezopasnosti","ОСБ ГИБДД","internal-security.json"),
]
NOTE = ("Текст перенесён из карточки Russia Online Wiki для Арбатского сервера. "
        "Вики указывает, что формулировки перенесены с официального форума без изменений; "
        "при расхождении приоритет имеет форумная редакция.")

def t(el):
    if el is None: return ""
    if isinstance(el, NavigableString): return re.sub(r"\s+"," ",str(el)).strip()
    return re.sub(r"\s+"," ",el.get_text(" ",strip=True)).strip()

def rtf_escape(s):
    out=[]
    for ch in s:
        n=ord(ch)
        if ch=="\\": out.append(r"\\")
        elif ch=="{": out.append(r"\{")
        elif ch=="}": out.append(r"\}")
        elif n<128: out.append(ch)
        else:
            for i in range(0,len(ch.encode("utf-16-le")),2):
                u=int.from_bytes(ch.encode("utf-16-le")[i:i+2],"little")
                out.append(r"\u"+str(u if u<32768 else u-65536)+"?")
    return "".join(out)

def rtf(body):
    paras=[]
    for line in body.splitlines():
        n=len(line)-len(line.lstrip()); line=line.strip()
        if not line: continue
        m=re.match(r"^(\d+(?:\.\d+)*(?:\s*\([а-яa-z]\))?[.)]?)\s*(.*)$",line,re.I)
        v=(r"\b "+rtf_escape(m.group(1))+r"\b0 "+rtf_escape(m.group(2))) if m else rtf_escape(line)
        paras.append(r"\pard\li"+("540" if n>=2 else "0")+r"\sa100\sl276\slmult1\f0\fs20 "+v+r"\par")
    return r"{\rtf1\ansi\deff0\uc1{\fonttbl{\f0 Segoe UI;}}\viewkind4\f0\fs20 "+"".join(paras)+"}"

def render(li):
    clone=BeautifulSoup(str(li),"html.parser").find("li")
    for x in clone.find_all(["ul","ol"]): x.decompose()
    lines=[t(clone)] if t(clone) else []
    for x in li.find_all(["ul","ol"],recursive=False):
        lines.extend("    "+z for z in render_block(x).splitlines())
    return "\n".join(lines)

def render_block(node):
    if node.name in ("ul","ol"):
        return "\n".join(x for li in node.find_all("li",recursive=False) if (x:=render(li)))
    if node.name=="table":
        return "\n".join(" | ".join(t(c) for c in tr.find_all(["th","td"],recursive=False)) for tr in node.find_all("tr"))
    return t(node)

def meta(soup,label):
    prose=soup.select_one(".prose-wiki")
    for d in soup.find_all("dt"):
        if d.find_previous(class_="prose-wiki") is None and t(d)==label:
            dd=d.find_next_sibling("dd")
            if dd: return t(dd)
    return ""

def article(slug,title,num,anchor,chapter,caption,body,wiki,forum,updated,kind="Статья"):
    body=body.strip()
    return {"Id":f"arbatwiki:{slug}|{anchor}","Number":num,"Title":caption or f"Положение {num}",
      "Text":body,"RichTextRtf":rtf(body) if body else "","Note":"","Tags":[],"Favorite":False,"InMemo":False,
      "Chapter":chapter or "Общие положения","SourceRef":f"{title} — {num}","SourceLawTitle":title,
      "SourceUrl":wiki,"SourceForumUrl":forum,"SourceUpdated":updated,"SourceServer":"Арбатский","ArticleType":kind}

def parse(html,order,key,slug,code):
    s=BeautifulSoup(html,"html.parser"); p=s.select_one(".prose-wiki")
    if not p: raise ValueError("article content not found")
    title=t(s.find("h1")) or key
    server=meta(s,"Сервер"); count=int(re.sub(r"\D","",meta(s,"Статей")) or 0)
    dtype=meta(s,"Вид документа")
    upd=""
    for d in s.find_all("dt"):
        if t(d)=="Обновлено":
            upd=d.parent.get("title") or t(d.parent.find("dd")); break
    call=p.select_one(".callout"); forum=""
    if call:
        for a in call.find_all("a",href=True):
            if "forum.russia.online" in a["href"]: forum=a["href"]
    wiki=f"https://russiaonline.wiki/wiki/laws/{slug}"
    formal=bool(p.select("dl.law dt")); rows=[]; chapter=""; active=None; in_changes=False; history=[]
    for n in list(p.children):
        if not isinstance(n,Tag): continue
        cls=n.get("class") or []; heading=t(n) if n.name in ("h2","h3") else ""
        low=heading.lower()
        if "callout" in cls or "lead" in cls or n.name=="nav": continue
        if n.name in ("h2","h3") and "изменения в законе" in low:
            in_changes=True; active=None; continue
        if n.name=="h2":
            if "коротко и по-человечески" in low: break
            in_changes=False; chapter=heading; active=None; continue
        if in_changes:
            if n.name=="h3": history.append("\n"+heading)
            elif n.name in ("p","ul","ol","blockquote","table"):
                v=render_block(n)
                if v: history.append(v)
            continue
        if n.name=="h3":
            hid=n.get("id") or ""
            if hid.startswith("st-") and re.search(r"\d",heading):
                m=re.search(r"(\d+(?:\.\d+)*)",heading)
                num=m.group(1) if m else heading
                active=article(slug,title,num,hid,chapter,heading,"",wiki,forum,upd,"Дополнительная статья")
                rows.append(active); continue
            if re.match(r"^(глава|раздел|часть|\d+[.)])",low):
                chapter=heading; active=None
            elif re.match(r"^(подписано|утверждено|авторство|дата:|начальник|генерал|специальное звание)",low):
                active=None
            elif active is not None and heading:
                active["Text"]=(active["Text"]+"\n" if active["Text"] else "")+heading
            else: chapter=heading
            continue
        if n.name=="dl" and "law" in cls:
            dts=n.find_all("dt",recursive=False); dds=n.find_all("dd",recursive=False); made=[]
            for i,d in enumerate(dts):
                raw=t(d); num=re.sub(r"^Статья\s*","",raw,flags=re.I).strip(" .№") or str(len(rows)+1)
                caption=t(dds[i]) if i<len(dds) else ""
                anchor=d.get("id") or (d.find("a").get("href","").lstrip("#") if d.find("a") else f"row-{len(rows)+1}")
                is_body=len(caption)>125 or (len(caption)>70 and caption.endswith((".",";",":")))
                body=caption if is_body else ""
                display=caption[:100].rstrip() if is_body else caption
                a=article(slug,title,num,anchor,chapter,display,body,wiki,forum,upd)
                cm=re.search(r"Глава\s+([IVXА-Я0-9]+)",chapter,re.I)
                if cm: a["Number"]=f"Гл. {cm.group(1)} · ст. {num}"
                rows.append(a); made.append(a)
            active=made[-1] if made else active
            continue
        if n.name in ("ul","ol") and "law-points" in cls and not formal:
            for li in n.find_all("li",recursive=False):
                body=render(li)
                if not body: continue
                m=re.match(r"^\s*(\d+(?:\.\d+)*(?:\s*\([а-яa-z]\))?)(?:[.)\s]+)",body,re.I)
                num=m.group(1) if m else str(len(rows)+1)
                cap=re.split(r"\.\s|:\s| — | – ",body,maxsplit=1)[0][:100].strip() or f"Правило {num}"
                active=article(slug,title,num,li.get("id") or f"rule-{len(rows)+1}",chapter,cap,body,wiki,forum,upd,"Правило")
                rows.append(active)
            continue
        if active is not None:
            value=render_block(n)
            if value: active["Text"]=(active["Text"]+"\n" if active["Text"] else "")+value
    if history:
        rows.append(article(slug,title,"История","change-history","История изменений","История изменений (справочно)",
            "\n".join(history),wiki,forum,upd,"История изменений (справочно)"))
    for a in rows:
        a["Text"]=a["Text"].strip()
        if not a["Text"] and len(a["Title"])>55: a["Text"]=a["Title"]
    doc={"Order":order,"Category":title,"Code":code,"LawTitle":title,"Server":"Арбатский",
      "DocumentType":dtype,"SourceServer":server,"SourceUrl":wiki,"SourceForumUrl":forum,
      "SourceUpdated":upd,"SourceStatus":"Опубликовано в каталоге Арбатского сервера; приоритет у редакции официального форума",
      "SourceAuthority":NOTE,"CatalogArticleCount":count,"ArticleCount":len(rows),"Articles":rows}
    return doc

def fetch(slug):
    url=f"https://russiaonline.wiki/wiki/laws/{slug}"; errors=[]
    for attempt in range(4):
        try:
            req=Request(url,headers={"User-Agent":"Mozilla/5.0 (compatible; RussiaOnlineStudio/1.0)","Accept":"text/html"})
            with urlopen(req,timeout=40) as r: return r.read().decode(r.headers.get_content_charset() or "utf-8","replace")
        except (HTTPError,URLError,TimeoutError) as e:
            errors.append(str(e)); time.sleep(1+attempt)
    raise RuntimeError(f"{url}: {'; '.join(errors)}")

def medical_fallback():
    path=EXT/"medical.json"
    old=json.loads(path.read_text(encoding="utf-8")); title='Федеральный закон "О статусе медицинских учреждений"'
    slug="arbat-fz-o-statuse-meditsinskih-uchrezhdeniy"; wiki=f"https://russiaonline.wiki/wiki/laws/{slug}"
    forum="https://forum.russia.online/threads/federal-nyi-zakon-o-statuse-meditsinskih-uchrezhdeniy.41962/unread"
    rows=[]
    for i,src in enumerate(old.get("Articles",[]),1):
        a=dict(src); uid=str(a.get("SourceUID") or a.get("Id") or a.get("Number") or i)
        a.update({"Id":f"arbatwiki:{slug}|{uid.split('|',1)[-1]}","SourceLawTitle":title,
          "SourceRef":f"{title} — {a.get('Number',i)}","SourceUrl":wiki,"SourceForumUrl":forum,
          "SourceUpdated":"22 ноября 2026 (карточка каталога; HTML-импорт недоступен)",
          "SourceServer":"Арбатский","Chapter":a.get("Chapter") or "Без главы в источнике",
          "ArticleType":"Статья","RichTextRtf":a.get("RichTextRtf") or rtf(a.get("Text","")),
          "Note":a.get("Note") or "Резервная копия темы Arbat 41962; сверить с актуальной карточкой."})
        rows.append(a)
    return {"Order":26,"Category":title,"Code":"Медицинские учреждения","LawTitle":title,"Server":"Арбатский",
      "DocumentType":"Федеральный закон","SourceServer":"Арбатский","SourceUrl":wiki,"SourceForumUrl":forum,
      "SourceUpdated":"22 ноября 2026 (карточка каталога; HTML-импорт недоступен)",
      "SourceStatus":"Резервная копия Arbat topic 41962; требуется сверка с актуальной карточкой",
      "SourceAuthority":NOTE,"CatalogArticleCount":45,"ArticleCount":len(rows),"Articles":rows}

def main():
    EXT.mkdir(parents=True,exist_ok=True); manifest=[]; categories=set(); total=0
    for order,key,slug,code,file in PAGES:
        try: doc=parse(fetch(slug),order,key,slug,code)
        except Exception:
            if key!="medical": raise
            doc=medical_fallback()
        if doc["Server"]!="Арбатский": raise RuntimeError(f"{key}: server mismatch")
        if doc["Category"] in categories: raise RuntimeError("duplicate document heading "+doc["Category"])
        categories.add(doc["Category"])
        (OUT/file).write_text(json.dumps(doc,ensure_ascii=False,indent=2),encoding="utf-8")
        total+=doc["ArticleCount"]
        manifest.append({k:doc.get(k) for k in ("Order","Category","LawTitle","Server","DocumentType","SourceUrl","SourceForumUrl","SourceUpdated","SourceStatus","ArticleCount","CatalogArticleCount")}|{"File":file})
        print(f"{order:02}. {doc['Category']}: {doc['ArticleCount']} entries; catalogue shows {doc['CatalogArticleCount']}")
        time.sleep(.25)
    if len(manifest)!=27: raise RuntimeError(f"expected 27 documents, got {len(manifest)}")
    (DATA/"law_catalog.json").write_text(json.dumps(manifest,ensure_ascii=False,indent=2),encoding="utf-8")
    print(f"Imported {len(manifest)} Arbat documents, {total} searchable entries.")
if __name__=="__main__": main()
