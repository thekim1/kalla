# U1: crawler about page (draft) and the umea.se robots.txt check

Register item U1 (`../postponed-decisions.md`). Prepared by the main session 2026-10-30, for the web editors and the
platform team. The page text is Swedish because it is published on umea.se; the notes are English.

## robots.txt check (done 2026-10-30)

`https://www.umea.se/robots.txt` (200, `text/plain`, 16 lines) has one group, `User-agent: *`, with 14 `Disallow`
lines for print views, contact and session/state query parameters, bookmarks, cart and image actions (for example
`/*.html.printable`, `/*?sv.state=*`, `/*;jsessionid=*`), and `Sitemap: https://www.umea.se/sitemapindex.xml`. There
is no group for `UmeaKommunBot` and no `Crawl-delay`. **The agent is not blocked.** The disallowed URLs are ones our
crawler should skip anyway; it obeys them through the `*` group (connectors design §5, RFC 9309). Re-check before the
first production crawl (the file can change).

## Still open for U1

- **The page's URL and owner** (web editors), for example `https://www.umea.se/om-webbplatsen/umeakommunbot`. The user
  agent then becomes `UmeaKommunBot/1.0 (+<that URL>)` (`archive/area-plans/ingestion-worker.md` §10).
- **Contact address** for site owners (a function mailbox, not a person).
- Agreement that the night window and rates below are acceptable to the web team (connectors design §6.3, §6.5).

## Draft page text (Swedish)

> **UmeaKommunBot – Umeå kommuns sökrobot**
>
> UmeaKommunBot hämtar offentliga sidor och dokument från Umeå kommuns webbplatser, och från andra webbplatser som
> kommunen själv har valt ut, för att kommunens digitala assistenter ska kunna svara på frågor med hänvisning till
> källan. Roboten känns igen på användaragenten `UmeaKommunBot/1.0`.
>
> **Så tar roboten hänsyn till din webbplats**
>
> - Den följer `robots.txt` enligt standarden RFC 9309. Regler för `UmeaKommunBot` gäller före regler för `*`.
> - Den hämtar i lugn takt: högst 10 sidor per sekund från kommunens egna webbplatser och högst 1 sida per sekund
>   från andra webbplatser. En `Crawl-delay` i `robots.txt` respekteras när den sänker takten (upp till 60 sekunder).
> - Hela webbplatser gås igenom nattetid, mellan 22.00 och 06.00.
> - Den frågar bara efter ändrade sidor när servern stöder det (`If-None-Match`, `If-Modified-Since`).
> - Den läser bara det som är öppet för alla. Den loggar inte in och skickar inte formulär.
>
> **Vill du begränsa eller stoppa roboten?**
>
> Lägg till en regel i din webbplats `robots.txt`, till exempel:
>
> ```
> User-agent: UmeaKommunBot
> Disallow: /
> ```
>
> Ändringen gäller från robotens nästa besök, senast inom ett dygn. Kontakta oss om du har frågor eller ser problem:
> **[funktionsadress]**.

Facts behind the text: robots.txt per RFC 9309 with the `UmeaKommunBot` group before `*`, and `Crawl-delay` honoured
only when it lowers the rate, capped at 60 s (connectors design §5); 10 req/s for municipal hosts, 1 req/s for
external ones, one budget per host shared by every source (§6.3); the night window `22:00–06:00` Europe/Stockholm
(§6.5); conditional GET (§5); only public content, no forms or sign-in (egress guard, §7). The "within a day" claim
assumes robots.txt is refreshed daily (`crawl/{host}/robots-{fetchedAt}.txt`, data-model §9). Confirm all of these
against the P1 code before the page is published.
