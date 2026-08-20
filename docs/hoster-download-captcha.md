# Hoster Download-Captcha Research Matrix

The register behind the upload wizard's **"Download captcha?"** column and each pipeline's
`IFileHosterPipeline.DownloadCaptcha` declaration. One row per shipping hoster; the coverage test
`HosterDownloadCaptchaTests.EveryHoster_MatchesTheResearchMatrix` pins every pipeline's declared
value to this matrix, so neither can drift alone.

**What the verdict means.** Whether a FREE/ANONYMOUS downloader — the person a shared link is for
— must actively solve a challenge (image/checkbox reCAPTCHA, hCaptcha, an interactive Turnstile
widget, a custom puzzle) before the host hands over the file. Countdown timers, plain download
buttons, click-through warnings, passwords, speed caps, and automatic CDN browser checks
(Cloudflare interstitials, invisible reCAPTCHA v3 badges) do NOT count. The model reports the
host's ordinary free/anonymous download flow and intentionally ignores the uploader's credentials.

**Evidence bar** (same honesty rule as the "Kept for" column):

- `Required` — the host's own pages state a captcha on free downloads, or sell "no captcha" as a
  premium perk, or a live free-download flow demonstrably gates link/file delivery behind an
  interactive challenge (a widget merely embedded but not gating anything is not enough).
- `NotRequired` — the share link IS the raw file bytes, or the host's own docs say downloads are
  captcha-free, or the ordinary free-download flow was walked with no challenge anywhere — to the
  bytes themselves, or to the revelation of the final direct file URL.
- `Unknown` — nothing met either bar. A researched-and-inconclusive row says so in its notes;
  never guess from platform family (XFS sites configure captcha per site — Send.now, UpZur,
  Uploadrar, FILEAXA, DailyUploads and Filehoster.io are all captcha-free XFS-family hosts).

Verdicts describe the ORDINARY flow: abuse-triggered exceptions (rate-limit or virus-flag
challenges, e.g. Pixeldrain's and DropMeFiles') don't flip a `NotRequired`.

**Maintenance.** A new hoster needs a row here, and — for a Required/NotRequired verdict — a
`DownloadCaptcha` declaration on its pipeline with the crux of the evidence in the member's
remarks. A researched-but-inconclusive host gets ONLY the `Unknown` row here (its pipeline stays
on the inherited default; a member that restates the default adds no behavior). The coverage test
parses this table and fails the build until the catalogue, the table, and the pipelines agree.
Re-verify a row when a host redesigns its download flow; update `Checked` when you do.

| Hoster | Verdict | Confidence | Checked | Evidence |
|---|---|---|---|---|
| 1Fichier | Required | high | 2026-08-20 | Pricing page (1fichier.com/tarifs.html) plan comparison: Guests get "Captcha", Premium gets "No captcha". |
| Alfafile | Required | high | 2026-08-20 | Live free flow: start_timer redirects to /file/…/captcha with a Cloudflare Turnstile widget; page states "Premium users download files without captcha codes!". |
| BowFile | NotRequired | medium | 2026-08-20 | The ordinary free flow is a 3 s countdown revealing the plain download redirect — the captcha decision point passed with no challenge at any stage; upgrade page sells "no waiting time", not captcha removal. (Final bytes unfetchable as a bot — a pop-under loop, different friction.) |
| BRupload | Required | high | 2026-08-20 | Premium compare table (brupload.net/premium.html): "Sem Captcha" crossed for Visitante/Registrado, ticked only for Premium. |
| BtaFile | Required | high | 2026-08-20 | Live free step 2 (op=download2) renders the stock XFS image captcha (input name="code" class="captcha_code" + positioned digit spans). |
| Buzzheavier | NotRequired | high | 2026-08-20 | Probe upload's public page: Download is a plain tokenized link that redirected straight to the bytes (Content-Disposition: attachment); no captcha step, no timer. |
| Catbox | NotRequired | high | 2026-08-20 | Share link IS the raw file: files.catbox.moe/<name> serves the bytes directly — no download page exists. |
| Clicknupload | Required | high | 2026-08-20 | Premium page compare table: "No downloads captcha" ticked for Premium, crossed for Free/Registered. |
| DailyUploads | NotRequired | high | 2026-08-20 | Live free flow (download1 → 30 s wait → download2) returned the file bytes; the form has only a countdown + adblock check, no captcha field. |
| DataNodes | Required | high | 2026-08-20 | Live free download page sets :has-captcha="true" and embeds an interactive Turnstile widget; premium plan lists "No ads or captchas". |
| DataVaults | Required | high | 2026-08-20 | Premium page "Downloads Captcha" row: yes-icon on Anonymous/Registered cards, no-icon on Premium/Premium PRO. |
| DDownload | Required | high | 2026-08-20 | Live file page: op=download2 form contains an interactive Turnstile widget (div class="dk-dl-captcha") plus a 60 s countdown. |
| DepositFiles | Required | high | 2026-08-20 | Host's own FAQ (depositfiles.com/faq.html): free flow = wait 60 s, then "enter the confirmation code (captcha)" before the Download File button. |
| DropMB | NotRequired | high | 2026-08-20 | Pingvin Share instance: GET /api/shares/{id}/files/{id}?download=true returns the bytes after an auto-issued share token; /api/configs exposes no captcha keys. Verified live. |
| DropMeFiles | NotRequired | high | 2026-08-20 | Live anonymous session: the server-rendered per-file drop1.dropmefiles.com/dl/… URL returned the bytes with no captcha step (hidden securimage overlay only arms for flagged traffic). |
| Easybytez | Required | high | 2026-08-20 | Premium compare table (easybytez.org/premium.html): "No downloads captcha" is a Premium/Premium Pro benefit, absent for Free/Registered. |
| EliteFile | Required | high | 2026-08-20 | Live free op=download2 form embeds the classic XFS positioned-digit captcha (input name="code" class="captcha_code") on a genuinely free file. |
| Emload | Required | high | 2026-08-20 | Live file page free widget: "Verify Captcha to Download" with an embedded reCAPTCHA v2 container + 59 s countdown; own core.js sells "No captcha codes" as premium. |
| Ex-Load | Required | high | 2026-08-20 | Host FAQ (ex-load.com/faq.html): free download = "wait for the timer, enter the captcha and get a link to download". |
| FILEAXA | NotRequired | high | 2026-08-20 | Premium page row "No downloads captcha" is checked for ALL tiers incl. anonymous; live free op=download2 302s straight to the bytes. |
| FileBoom | Required | high | 2026-08-20 | Operator's official API docs (keep2share.github.io/api, fboom.me base): free getUrl requires requestCaptcha/captcha_response, else 406 "Captcha required". |
| Filebin | NotRequired | high | 2026-08-20 | Own OpenAPI (filebin.net/api.yaml): GET /{bin}/{filename} 302s to a presigned S3 URL; the only gate is a click-through warning, not a captcha. |
| FileCat | Required | high | 2026-08-20 | Live API: POST api.filecat.net/dwnldreq answers {"captcha_needed":true} for a guest download of a non-premium file (reCAPTCHA v2 flow). |
| Filedot | Required | high | 2026-08-20 | Premium compare table (filedot.to/premium.html): "No downloads captcha" unavailable for Free/Registered, checked for Premium. |
| FileGarden | NotRequired | high | 2026-08-20 | Share link is direct bytes: HEAD on file.garden/<id>/<path> returns the raw content (no download page exists). |
| Filehoster.io | NotRequired | high | 2026-08-20 | Live anonymous probe: free op=download2 flow has a 1 s wait, zero captcha widgets, file downloaded on click; premium page marks "No downloads captcha" checked for Free too. |
| FileMirage | NotRequired | high | 2026-08-20 | Live probe: server-rendered download page has zero captcha markup and the share link is a direct file URL (hotlink embeds offered). |
| Filego | NotRequired | high | 2026-08-20 | Whole-app bundle (filego.io/assets/js/bundle.js) has no captcha of any kind; download is a direct navigation to /api/dl/file/<id>/<name>. |
| FileStore | Unknown | medium | 2026-08-20 | Researched, inconclusive: the apex serves an interactive Cloudflare challenge to non-browsers (excluded by rubric); a Wayback file page shows a plain op=download1 form, but the post-download1 step (where XFS sites place captcha) was unobservable. Premium pitch never mentions captcha. |
| Filestank | Required | high | 2026-08-20 | Live free flow: after the 30 s countdown, the continue step embeds a visible reCAPTCHA v2 widget in the download POST form. |
| GigaFile | NotRequired | high | 2026-08-20 | Support FAQ: "pressing download starts it immediately"; live upload's download page had no captcha and download.php served bytes (range-GET 206). |
| GigaPeta | Required | high | 2026-08-20 | Live free page (/dl/<id>): /js/download.js injects an image-digit captcha ("Type the digits from the image", captcha_key+captcha fields) into the download form after the timer. |
| Gofile | NotRequired | high | 2026-08-20 | Live probe as auto-created guest: Download button + direct fetch returned the bytes with zero captcha widgets in the DOM. |
| Hexload | Required | high | 2026-08-20 | Premium page lists "No Captcha, Timer, Waiting"; Plans_Comparison.html row "No downloads captcha" checked only for Premium. |
| HitFile | Required | high | 2026-08-20 | Host's own SPA free-download chunk (assets/FreePage-*.js) implements FreeDownloadCaptchaView ("Enter symbols from image") gated by showCaptcha. |
| Hostize | NotRequired | high | 2026-08-20 | Live probe: anonymous /api/shares/…/download 302s to a presigned S3 file URL (Content-Disposition: attachment); share-page chunk has no captcha. |
| Hxfile | Required | high | 2026-08-20 | Live file page: free op=download2 form contains a visible reCAPTCHA v2 widget (g-recaptcha div with sitekey) beside the countdown. |
| IcerBox | Required | high | 2026-08-20 | Host's own app.min.js: free download posts to dl/free/step2 with recaptcha_challenge_field/recaptcha_response_field (vcRecaptcha module). |
| Isracloud | Unknown | medium | 2026-08-20 | Researched, inconclusive: only premium-locked files were reachable ("available for Premium Users only"), so the free flow was unobservable; FAQ/premium pages neither mention captcha nor sell "no captcha". |
| KatFile | Required | high | 2026-08-20 | Live free flow (katfile.biz): op=download2 embeds an interactive Turnstile widget with the text "Premium users download files without reCAPTCHA"; premium page lists "Free of Captcha". |
| Keep2Share | Required | high | 2026-08-20 | Operator's official API docs (keep2share.github.io/api): guest/free getUrl returns error 30 "Captcha required"; flow mandates requestCaptcha + captcha_response. |
| kshared | Required | high | 2026-08-20 | Premium page (kshared.com/premium) lists "No captcha codes" among the premium benefits of every tier. |
| Litterbox | NotRequired | high | 2026-08-20 | Share link IS the raw file: litter.catbox.moe/<name> serves the bytes directly (catbox's temp sibling; no download page exists). |
| MediaFire | NotRequired | high | 2026-08-20 | Live file page: the DOWNLOAD button href is a direct downloadNNNN.mediafire.com CDN URL embedded in the initial HTML; no captcha widget. |
| MEGA | NotRequired | high | 2026-08-20 | MEGA's own MEGAcmd guide documents anonymous public-link downloads (mega-get, no login); the documented flow has no captcha step — free-tier limits are transfer quota. |
| MegaUp | NotRequired | medium | 2026-08-20 | The ordinary free flow is a 2 s countdown revealing the final direct download.megaup.net link with no challenge anywhere; upgrade page sells "Direct downloads. No waiting.", no captcha mention. (The CDN serves bots a Cloudflare interstitial — automatic, excluded by rubric.) |
| NitroFlare | Required | high | 2026-08-20 | Live download page: the Free Download tier explicitly lists "Captcha request" plus "Ticket-waiting (180s)" vs the premium tier. |
| Pixeldrain | NotRequired | high | 2026-08-20 | Own API docs (pixeldrain.com/api): GET /api/file/{id} "Returns the full file" unauthenticated; captcha exists only as rate-limit/virus-flag exceptions. |
| PreFiles | Required | high | 2026-08-20 | Pricing comparison (prefiles.com/pricing) lists "No downloads captcha" as a PRO Membership perk, absent from the free tier. |
| Qu.ax | NotRequired | high | 2026-08-20 | Raw file served directly at qu.ax/x/<code>.<ext> (verified with a live upload); the viewer page has no captcha markup. |
| Rapidgator | Required | high | 2026-08-20 | Live free flow: SLOW SPEED DOWNLOAD → 175 s countdown → /download/captcha "Enter code" form; page states "Premium users download files without captcha codes!". |
| Send.now | NotRequired | high | 2026-08-20 | Host's own demo file page (linked from send.now/features): single Download form with no captcha markup — submitting it immediately started the download. |
| Sendspace | NotRequired | high | 2026-08-20 | Live probe: the file page served a cookie-less client a server-rendered direct fsNN.sendspace.com/dl link that returned the bytes; no captcha markup. |
| SubyShare | Required | high | 2026-08-20 | Premium compare table (subyshare.com/premium.html) row "Solve Captcha on download": Free = YES, Registered = YES, Premium = "None!". |
| Temp.sh | NotRequired | high | 2026-08-20 | Live probe: the share page's plain POST form returns the raw file (Content-Disposition: attachment); no captcha in the flow. |
| TeraBytez | Required | high | 2026-08-20 | Premium compare table (terabytez.org/premium.html) row "No downloads captcha": check for Premium only, cross for Free/Registered. |
| TezFiles | Required | high | 2026-08-20 | Operator's official API docs (keep2share.github.io/api) explicitly cover tezfiles.com: guest getUrl returns error 30 "Captcha required". |
| TmpFiles | NotRequired | high | 2026-08-20 | Live probe: the download page has only a plain /dl/ link and no captcha; the /dl/ URL returns the raw bytes. |
| Transfer.it | NotRequired | high | 2026-08-20 | Live end-to-end anonymous transfer: recipient page started the download on click with zero captcha widgets or scripts; FAQ confirms no account needed. |
| Turbobit | Required | high | 2026-08-20 | Host's own SPA free-download chunk (assets/FreePage-*.js) defines FreeDownloadCaptchaView (AppCaptcha: Turnstile/reCAPTCHA/hCaptcha) whose captchaResult gates downloadUrlPrepared. |
| Udrop | NotRequired | high | 2026-08-20 | Host FAQ (udrop.com/faq): "Do my downloaders have to wait on a timer or solve CAPTCHAs? No. … Clicking your link triggers an immediate download stream". |
| Ufile | Unknown | medium | 2026-08-20 | Researched, inconclusive: the download page carries only an INVISIBLE reCAPTCHA (excluded by rubric), FAQ never mentions captcha, and every sample file was premium-locked/expired, so a clean free download couldn't be demonstrated either way. |
| Upload.ee | NotRequired | high | 2026-08-20 | Live file page embeds the direct /download/<id>/<token>/<name> link which serves the file; no captcha markup on the page. |
| UploadGIG | Required | high | 2026-08-20 | Live download page: free section instructs "please check 'i'm not a robot', then click download button" (reCAPTCHA checkbox) plus a 60 s wait. |
| UploadHive | Required | high | 2026-08-20 | Premium page (uploadhive.com/premium/) lists "No downloads captcha" and "No downloads delay" as premium-only perks. |
| UploadNow | Unknown | medium | 2026-08-20 | Researched, inconclusive: the download-page bundle (cdn.uploadnow.io …/share-*.js) contains no captcha markup and the app's only reCAPTCHA is login-side Firebase Auth — but the Firebase-driven flow never reached file delivery, and bundle absence alone doesn't prove the complete delivery route, so neither bar was met. |
| Uploadrar | NotRequired | high | 2026-08-20 | Premium compare row "No downloads captcha" is green for free & registered tiers; live free op=download2 yields a direct fsNN.uploadrar.com link, form has only a countdown. |
| Uploady | Required | high | 2026-08-20 | The file download page's own free-column feature list reads "120s waiting time / Speed limited to 100 KB/s / Ads & captcha required" (premium: "No ads or captcha"). |
| UpZur | NotRequired | high | 2026-08-20 | Premium comparison (upzur.com/premium) marks "No downloads captcha = Yes" for the FREE tier; live op=download2 yields a direct octet-stream link with no captcha. |
| UsersDrive | Required | high | 2026-08-20 | Live "Download server" page: the free op=download2 form embeds an interactive Turnstile widget directly above the "Create Download Link" button. |
| Upstore | Required | high | 2026-08-20 | Premium page (upstore.net/premium/) lists "Captcha" under the free "Slow download" column vs "No captcha" under premium. |
| VikingFile | Required | high | 2026-08-20 | File page renders a Turnstile widget in div id="captcha-download" (turnstile.render) that must fire its callback before the hidden #download-link is revealed. |
| Webshare | NotRequired | high | 2026-08-20 | Live probe: anonymous POST /api/file_link/ (ident only) returned a direct free.NN.dl.wsfiles.cz link that served the bytes; the file page shows a plain download button, no captcha. |
| Wormhole | NotRequired | high | 2026-08-20 | Live end-to-end test: the recipient page started the download immediately on click; no captcha widget; E2EE — the key rides the URL fragment (wormhole.app/security). |
| World Files | Required | high | 2026-08-20 | Premium comparison table (world-files.com/premium.html) shows "No downloads captcha" checked for Premium only. |
| Xubster | Unknown | medium | 2026-08-20 | Researched, inconclusive: free-download attempts on four live files all answered "This file is available for Premium Users only", so the free-flow captcha step was unobservable; premium page and FAQ are silent on captcha. |
