import urllib.request
import urllib.parse
import re
import os
import json
from html.parser import HTMLParser

BASE_URL = "https://whatsapp-rust.jlucaso.com/"
START_URL = "https://whatsapp-rust.jlucaso.com/introduction"

req = urllib.request.Request(START_URL, headers={'User-Agent': 'Mozilla/5.0'})
try:
    with urllib.request.urlopen(req) as resp:
        content = resp.read().decode('utf-8')
        print(f"Fetched {START_URL}, length: {len(content)}")
        # Look for table of contents / sidebar links
        hrefs = re.findall(r'href=["\']([^"\']+)["\']', content)
        print(f"Total hrefs: {len(hrefs)}")
        internal_links = set()
        for h in hrefs:
            full = urllib.parse.urljoin(START_URL, h)
            if full.startswith(BASE_URL):
                clean_url = full.split('#')[0].split('?')[0]
                internal_links.add(clean_url)
        print("Internal links:")
        for link in sorted(internal_links):
            print(" -", link)
except Exception as e:
    print("Error:", e)
