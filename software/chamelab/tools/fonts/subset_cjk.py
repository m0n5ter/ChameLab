"""Subset Noto Sans SC / JP / KR to the characters the app's Chinese, Japanese and Korean
translations use, for the web app (the browser offers Avalonia no system fonts to fall back on).

Run again after changing Strings.zh-Hans/ja/ko.resx or the language names in
LocalizationService.cs:

    pip install fonttools
    python subset_cjk.py <dir with NotoSansSC_400Regular.ttf, NotoSansSC_600SemiBold.ttf, … JP, KR>

The full fonts come from Google Fonts (or the @expo-google-fonts/noto-sans-sc|jp|kr npm packages).
Output: ChameLab.Browser/Assets/Fonts/NotoSans{SC,JP,KR}-{Regular,SemiBold}.ttf
"""
import pathlib
import re
import sys
import xml.etree.ElementTree as ET

from fontTools import subset

ROOT = pathlib.Path(__file__).resolve().parents[2]
STRINGS = ROOT / 'ChameLab' / 'Localization'
OUT = ROOT / 'ChameLab.Browser' / 'Assets' / 'Fonts'
FONTS = {'SC': 'zh-Hans', 'JP': 'ja', 'KR': 'ko'}
WEIGHTS = {'Regular': '400Regular', 'SemiBold': '600SemiBold'}


def is_cjk(ch: str) -> bool:
    c = ord(ch)
    return (0x2E80 <= c <= 0x9FFF      # CJK radicals, punctuation, kana, Hangul jamo, ideographs
            or 0xAC00 <= c <= 0xD7AF   # Hangul syllables
            or 0xF900 <= c <= 0xFAFF   # CJK compatibility ideographs
            or 0xFF00 <= c <= 0xFFEF)  # full-width forms


def translation_text(lang: str) -> str:
    root = ET.parse(STRINGS / f'Strings.{lang}.resx').getroot()
    return ''.join(v.text or '' for v in root.iter('value'))


# Every language name is shown in the picker whatever the UI language, so each font covers them.
names = ''.join(re.findall(r'\("[^"]+", "([^"]+)"\)', (ROOT / 'ChameLab' / 'Services' / 'LocalizationService.cs').read_text('utf-8')))
# Kana and CJK punctuation are small and turn up in user text too.
common = ''.join(map(chr, range(0x3000, 0x3100))) + ''.join(map(chr, range(0xFF01, 0xFF5F)))

src = pathlib.Path(sys.argv[1])
OUT.mkdir(parents=True, exist_ok=True)
for script, lang in FONTS.items():
    chars = sorted({ch for ch in translation_text(lang) + names + common if is_cjk(ch)})
    for weight, folder in WEIGHTS.items():
        font = next(src.rglob(f'NotoSans{script}_{folder}.ttf'))
        out = OUT / f'NotoSans{script}-{weight}.ttf'
        options = subset.Options()
        options.layout_features = ['*']
        options.name_IDs = ['*']
        options.notdef_outline = True
        subsetter = subset.Subsetter(options)
        ttf = subset.load_font(str(font), options)
        subsetter.populate(unicodes=[ord(ch) for ch in chars])
        subsetter.subset(ttf)
        subset.save_font(ttf, str(out), options)
        print(f'{out.name}: {len(chars)} characters, {out.stat().st_size // 1024} KB')
