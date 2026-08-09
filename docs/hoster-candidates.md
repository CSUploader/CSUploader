# File-Hoster Expansion Candidates

_Derived from the [fynks/debrid-services-comparison](https://github.com/fynks/debrid-services-comparison) host list (328 hosts supported across 9 debrid services), assessed 2026-07-08._

This document outlines file-hosting sites CSUploader **could** add upload support for, based on the hosts that debrid services support. Each candidate was researched for whether it is alive and upload-capable today, its real upload mechanism, anonymous-vs-account requirement, captcha / Cloudflare gating, per-file limit, and how it maps onto CSUploader's existing pipeline architecture.

## How to read this

**Scope.** The debrid list mixes true upload hosts with download-only platforms. This document covers **file-upload hosts + consumer cloud storage** only. Download-only streaming (YouTube, Vimeo, Twitch…), social (Twitter, Reddit, Instagram…) and news sites were dropped — you can't upload an arbitrary file to them. Pure video-upload hosts are in an appendix (out of the chosen scope but technically upload-capable).

**Already excluded.** The hosters CSUploader already ships (46 as of 2026-08-02) and the 7
disabled-in-tree (DropGalaxy, ExtMatrix, FlashBit, Hotlink, ShareMods, TakeFile, UploadGIG) are not
repeated here.

> ✅ **As of 2026-08-02 every shipped hoster has moved real bytes from the client.** The last four
> outstanding — Clicknupload, Turbobit, DropMeFiles and Uploadrar — were confirmed together by real
> uploads. That matters most for DropMeFiles, whose failure mode was **silent**: all-200 responses, a
> link returned, and the drop page reading "Files were deleted due to unexpected error while
> uploading". A capture-derived reconstruction can't prove that path; only an upload can.

**Effort legend** (based on CSUploader's existing bases):

| Effort | Meaning |
|---|---|
| **Low** | Thin shim on `XFileSharingApiPipeline` (XFS family) or a trivial reuse of an existing primitive |
| **Medium** | New standalone REST pipeline (bespoke HTTP flow, like MediaFire / Pixeldrain / Gofile / Storage.to) |
| **High** | New OAuth / cloud-drive pattern — CSUploader has **none** yet (WebView consent + refresh-token storage) |
| **Blocked** | Cloudflare **managed** challenge (TLS-fingerprinted — proven dead-end, cf. TakeFile/UbiqFile), per-upload captcha, premium-only upload, or the service is dead |

**Confidence caveat.** Most mechanisms were corroborated from live probes + multiple open-source uploader implementations, but few had a full live round-trip from this environment (datacenter IPs are frequently bot-blocked). Before building any host, verify **anon-vs-account, exact per-file cap, and passive-vs-managed Cloudflare** against the live upload node. XFS clones especially: a "Low shim" assumes the host is genuinely stock XFileSharing and its upload node is not behind a managed challenge — confirm both.

---

## Final sweep — 2026-08-03

Every candidate row not already shipped or closed was re-tested, **with the decisive test rather than
a form scrape**: does an anonymous multipart post to the host's own node actually store a file? That
distinction matters — ShareMods was found anonymous by reading a homepage form that two earlier
sweeps had missed, and **easybytez.org renders a `utype=anon` form and still refuses**
(`uploads are not enabled for your account type`), so neither the presence nor the absence of a guest
form is evidence. Only the upload's answer is.

| Outcome | Hosts |
|---|---|
| **Refuses anonymous uploads** (probed, verdict from the node) | ~~easybytez.org~~ (**SHIPPED 2026-08-03 as an ACCOUNT host** — see its row below), filedot.to, kenfiles.com, fastfile.cc, terabytez.org, datavaults.co, clicknupload |
| **No anonymous route offered at all** (no guest form, no keyless node) | ~~elitefile.net~~ (**SHIPPED 2026-08-03 as an ACCOUNT host**), filefox.cc, megaup.net, rapidrar.com, filecat.net, fileq.net, uploadbank.com, wipfiles.net |
| **Down / unreachable** | drop.download (502), rosefile.net (521), fikper.com (522), cyberdrop.me + worldbytez.com (no DNS record) |
| **Cloudflare-challenged to this client** | apkadmin.com, datanodes.to, modsbase.com, sharemods.com (built, disabled) |
| **Account + captcha on every upload** | depositfiles.com (`upload.php` behind Turnstile; its API answers `LoginInvalid`), subyshare.com (premium-only upload) |
| **Registration impossible** | kenfiles.com, koofr.eu, bunkr.cr |
| **Parked on the host's own breakage** | krakenfiles.com — still "Sorry, service unavailable." (re-checked 2026-08-02) |

> ⚖ **Amended 2026-08-03.** "Refuses anonymous" is not the same as "unbuildable", and this table
> conflated the two for one row: **easybytez.org shipped the same day as an account host**, once an
> account existed. The conclusion below is about the ANONYMOUS seam only. Any row marked "refuses
> anonymous" or "no anonymous route" remains buildable by anyone willing to register — a cost
> question, not a closed door.

> 🔎 **The list itself had a blind spot (2026-08-03).** It derives from a **debrid** index, which by
> construction catalogues DOWNLOAD hosts — so **transfer services and small anonymous drop hosts can
> never appear in it**, however thoroughly its rows are worked. Asking that question directly found
> **temp.sh** (4 GB, 3-day expiry) and **Litterbox** (1 GB, 72 h — catbox.moe's temporary sibling),
> both anonymous, both one multipart POST, both **SHIPPED the same day** and verified with real
> bytes. Also probed: **SwissTransfer** (50 GB, no account — but its container API validates a
> reCAPTCHA v3 token server-side: *"Captcha not valid"*, and v3 tokens are single-use, so it would
> mean a captcha per transfer → **blocked**) and **filemail** (needs a request token → not keyless).
> **tmpfiles.org SHIPPED 2026-08-03** (100 MB; documented API at /api). ⚠ Its `expire` field defaults
> to ONE HOUR — measured 47h59m with `expire=172800` versus 59 minutes without, so the maximum is
> always sent.
>
> **qu.ax SHIPPED 2026-08-04** and is the pick of the category: 256 MB and **PERMANENT**
> (`expiry=-1`; its form defaults to 30 days). ⚠ But it runs an **allowlist** — `.rar`/`.zip`/`.7z`
> and `.partN.rar` pass while **`.r00`, `.001`, `.sfv` and `.nfo` are refused**, so a classic
> multi-part set only half-uploads. It also de-duplicates by content hash.
>
> 🔚 **Second pass over this category, 2026-08-05 — nothing left.** The trick that found it in the
> first place (ask what the source list was incapable of containing) was run again against the two
> seams still open, and this time it produced nothing:
>
> | host | result |
> |---|---|
> | **x0.at** | **403 "Your upload was rejected"** for `.rar`, `.bin` AND `.txt` — a blanket refusal of this client, not an extension rule. Same wall as ShareMods. |
> | **envs.sh**, **bashupload.com** | no DNS record at all |
> | **oshi.at** | connection refused |
> | **ttm.sh** | 307 redirect loop, even with the method preserved across the hop |
> | **pomf.lain.la** | serves "Pomf has been shut down" |
>
> 0x0.st's clones were the promising idea — the original disabled uploads but its code is widely
> self-hosted — and they are simply gone or hostile. **A negative result from a repeated method is
> still worth recording:** the next person should not spend an afternoon rediscovering that these six
> are dead.
>
> ⚖ **What genuinely remains is a scope decision, not a candidate:** image hosts (Imgur anonymous via
> a shipped Client-ID, ImgBB via a key); the deferred cloud drives in Tier C; or the **E2E storage**
> services with published SDKs — **Filen.io** (10 GB free) and **Internxt** (1 GB) — which are
> account-based, crypto-heavy builds on the scale of the MEGA/transfer.it work, not shims.
>
> **The rest of the category is closed:** filetransfer.io doesn't work (user-tested); **0x0.st has
> disabled uploads outright** — *"uploads disabled because it's been almost nothing but AI botnet
> spam for the past few months"* (HTTP 503, 2026-08-04); **uguu.se refuses .rar AND .zip** (415
> "Filetype not allowed" — it takes .bin/.txt, which is no use for release sets); **fileditch**
> refuses connections on its upload host.
>
> The lesson is about the source, not the hosts: **when a list is exhausted, check what the list was
> incapable of containing.**

**Conclusion: the ANONYMOUS file-host seam is exhausted.** Nothing on this list is reachable, anonymous and buildable.
What remains is a choice about scope rather than a candidate to probe: **image hosts** (Imgur
anonymous via a shipped Client-ID, ImgBB via a key) if that's ever in scope, or the **cloud drives**
in Tier C — deferred, see the banner there for why the obvious design doesn't work.

## Recommended shortlist

The best first adds, ranked by value ÷ effort. All are alive, anonymous (no login needed), and ungated:

| # | Host | Effort | Why |
|---|---|---|---|
| 1 | **Buzzheavier** | Low | Simplest possible pipeline: a single raw `PUT` of the file body reusing `HttpHandler.UploadPutAsync`, parse the JSON `id`. Anonymous, no cap, no captcha, no managed Cloudflare. |
| 2 | **Send.cm** | Low | Classic XFS host — **anonymous upload verified live 2026-07-08** (`/api/upload/server` returned a keyless upload node). Maps 1:1 onto `XFileSharingApiPipeline`. ~20 GB free. |
| 3 | ~~**Filestank**~~ | — | **SHIPPED 2026-08-01.** Two of this row's four claims were wrong: it is **YetiShare**, not XFS, and it is **account-only**, not anonymous. The 20 GB *was* right — 20 GiB exactly, though the figure lives in the account's uploader, not on any public page. See the corrected Tier A1 row. |
| 4 | ~~**1Fichier**~~ | — | **SHIPPED 2026-07-29.** Note the cap correction below: anonymous is **5 GB**, not the ~300 GB first recorded here. |
| 5 | **Krakenfiles** | **PARKED** | ⏸ **Anonymous upload is disabled on THEIR side (2026-07-30; re-checked 2026-08-01, still off** — the homepage upload box still renders "Sorry, service unavailable." beside a live `maxFileSize: 1073741274`**).** The protocol was fully worked out — see the Tier B row — but nothing can be built until it comes back. |
| 6 | ~~**Sendspace**~~ | — | **SHIPPED 2026-08-01.** Anonymous, no captcha, 300 MiB — scrape the homepage's single-use ticket, post the site's own form, and the reply is the result page. See the Tier B row, including the two wrong turns it took to get there. |
| 7 | ~~**VikingFile**~~ | — | **SHIPPED 2026-07-30.** Documented API held up: reused the Storage.to presigned-PUT + ETag primitives as predicted. |
| 8 | ~~**Uploady**~~ / **WIP Files** | Low | Uploady **SHIPPED 2026-07-27** — account-only in the end (its anonymous upload is broken server-side, in a real browser too). WIP Files renders no anonymous form either, probed 2026-08-01 — see its Tier A1 row. |
| 9 | ~~**Turbobit**~~ | — | **SHIPPED 2026-08-01.** The sibling theory held exactly: same platform, same `kohanasession7` cookie, same endpoints. Account-only here — the one value that differs is `apptype` (`fd1`, not HitFile's `fd2`). |
| 10 | **Koofr** | Medium | The one cloud drive that is **not** OAuth — HTTP Basic app-password + plain REST, models cleanly on the MediaFire/Pixeldrain account pipelines. 10 GB free. |

If image hosting is ever in scope, **Imgur** (anonymous via a shipped Client-ID) is a small Medium add.

---

## Tier A — Low effort: XFileSharing (XFS) shims

All map onto `XFileSharingApiPipeline` (classic variant unless noted). A new host is a thin shim; the risk is per-host liveness / anonymous-availability / passive-vs-managed Cloudflare, to confirm at build time.

### A1 · Anonymous-capable, low/no gating (best value)

> ⚠ **This whole section is largely STALE as of 2026-07-31 — treat every "anonymous" claim below as
> unverified until re-probed.** A sweep of ten of these (dailyuploads, easybytez, fileaxa, megaup,
> drop.download, apkadmin, elitefile, fastfile, rosefile, uploadrar) found **not one** still rendering
> an anonymous `upload.cgi …utype=anon` form. Clicknupload — the only one of the batch that still
> answers the XFS `?op=api_get_limits` marker at all — accepts an anonymous POST and replies
> `uploads are not enabled for your account type`; its advertised `MaxUploadFilesize 2048` describes
> REGISTERED users. That matches DropGalaxy and Uploady, both of which turned out the same way.
> The XFS family has broadly moved to account-required uploads, so budget an account (and a live
> re-probe) before planning any host below as "anonymous".
>
> ⚠ **But the converse is ALSO untrue — do not read this as "anonymous XFS is dead".** That sweep only
> covered hosts this doc already CLAIMED were anonymous. **Usersdrive**, filed below in the
> account-only tier A2, turned out to serve a live anonymous upload at 5250 MB and shipped on
> 2026-08-01. A host's listed tier is a hypothesis; probe the homepage for a `utype=anon` form before
> believing either direction.
>
> ⛔ **CORRECTION (2026-08-02, later): the two "clean negative" sweeps below tested the WRONG THING.**
> Both looked for a static `utype=anon` / `upload.cgi` form in the served HTML. FILEAXA renders no
> such form and uploads anonymously anyway — its uploader is JS-driven. Re-probing the same hosts
> with the right question (`GET /server`, the keyless xfspro node lookup) found **five** that answer
> it: **dailyuploads.net** and **fileaxa.com** (both now SHIPPED and verified with real bytes), plus
> **filedot.to**, **kenfiles.com**, **terabytez.org** and **fastfile.cc** — node lookup confirmed
> keyless, upload not yet tried. So the anonymous seam was never closed; the test was bad. Absence of
> a form in HTML says nothing when the uploader is a script.
>
> ⚖ **Follow-up (2026-08-02): answering `/server` does NOT imply anonymous.** filedot.to answers the
> lookup keylessly and then refuses the upload — posting the anonymous shape to its own node returns
> `[{"file_code":"undef","file_status":"uploads are not enabled for your account type"}]`, and so does
> `utype=reg` without a session. It also runs *classic* `upload.cgi`, not xfspro's chunked
> `put_chunk.cgi`, so the node lookup is shared family plumbing rather than an anonymity signal. It is
> now **SHIPPED as account-only** (`FiledotPipeline`, web-form/sign-in path). The correction above
> stands — the lookup is still the right question to ask — but the answer to check is the upload's,
> not the lookup's. **All three of the rest were then probed the same way (2026-08-02) and all three
> are account-only too**: kenfiles.com, terabytez.org and fastfile.cc each take an anonymous
> `put_chunk.cgi` and answer `{"status":"OK"}`, then refuse at `import_file` with `uploads are not
> enabled for your account type`. So does datavaults.co, found later on the same shape. **A node
> accepting bytes proves nothing** — the same late-enforcement trap as Uploadrar's extension list.
> That closes this seam: five hosts answered `/server`, exactly two upload anonymously (FILEAXA,
> DailyUploads). **TeraBytez is now SHIPPED as account-only** (`TeraBytezPipeline`).
>
> ✅ **The four rows this section had never actually probed are now done too (2026-08-02):
> UploadBank, TeraBytez, Fileq and Filecat.** None renders a `utype=anon` / `upload.cgi` form, and
> `?op=api_get_limits` answers with an HTML page on all four rather than the XFS key=value block —
> so none is a classic XFS API host either. Fileq is the only one with a substantial modern
> uploader (`upload-form`/`uploadBox`, JS-driven) and it fronts login/register. **Combined with the
> A3 sweep, every anonymous claim in this document has now been checked: none survives.** Anything
> left in tiers A1/A2/A3 needs an account before it can be planned at all.
>
> ⚠ **And the FAMILY label is a hypothesis too.** **Filestank**, listed here as "stock XFS", turned out
> to run **YetiShare** — a different script entirely, with its own `/api/v2/` and no XFS endpoint of any
> kind. Check the page source for the platform's own markers (`themes/spirit` + `/api/v2/` = YetiShare;
> `?op=` routes + `upload.cgi` = XFS) before budgeting a host as "a thin shim on the XFS base".

| Host | Domain | Free cap | Gating | Conf. | Note |
|---|---|---|---|---|---|
| **Send.cm** | send.cm (→ send.now) | ~20 GB | CF-passive | high | Anon verified live 2026-07-08. tusfiles/file.cm/sendit lineage. |
| ~~**Filestank**~~ | filestank.com | ❌ **account-only** (20 GiB/file) | reCAPTCHA (login) | high | **SHIPPED 2026-08-01** (FilestankPipeline) — and it is **not an XFS host at all**, so it does not belong in this tier. It runs **YetiShare** (`themes/spirit`), whose uploader is blueimp jQuery-File-Upload against a separate storage node. Shipped on the **session-cookie** path (capture-verified 2026-08-01): WebView sign-in at `/account/login` → per-upload `GET /assets/js/uploader.js`, which is generated per session and carries all three moving parts (node URL `strN.filestank.com/ajax/file_upload_handler?…csaKey1=…&csaKey2=…`, `_sessionid`, `cTracker` — all of them rotate) → multipart POST `files[]` + those fields → `[{…,"url":…,"error":null}]`. **The node takes no cookie at all** — `_sessionid` in the body is the whole credential there. Cap is 20 GiB, read from the uploader's own `maxFileSize: 21474836480`; over 100 MB it chunks (`maxChunkSize: 100000000` + `Content-Range`), which is implemented but not yet capture-verified. It also publishes a full `/api/v2/` (authorize with two 64-char keys → `access_token` → `file/upload`) and was FIRST built on it — but the account area exposes no page that yields those keys, and "find two 64-character keys" is a worse first-run credential than signing in (the DDownload lesson). Storage stats: `POST /account/ajax/get_account_file_stats`. **⏳ Anonymous EXISTS after all** (capture 2026-08-01): YetiShare auto-creates a "trial account" (`trial_username`/`trial_hash` cookies, banner "We've created you a trial account… any uploads will be publicly accessible"), and the upload wire shape is IDENTICAL — only the cap differs, 1 GiB vs 20 GiB. NOT built yet because **nothing reachable mints the trial account**: a fresh guest session gets `/account?triggerUpload=1` → 302 `/account/login`, and `/account/trial`, `/register/trial`, `/ajax/create_trial_account`, `/upload` are all 404. Note the tell — a signed-out visitor IS served a complete, valid-looking `uploader.js` ticket, with `uploaderMaxSize = 0`. That figure is now read per upload and enforced (0 / 1 GiB / 20 GiB by tier). **⚠ Daily upload COUNT limit (~10 files/day free, observed 2026-08-01)** — invisible until spent, then the node answers `{"name":"Max uploads reached.","error":"You have reached the maximum permitted uploads for today."}` inside a 200, *after* a chunk has been pushed. Recognised and remembered per account for the day so the rest of a batch fails free. **This makes Filestank a poor batch target on a free account** — reconsider its value if the limit can't be raised. |
| **WIP Files** | wipfiles.net | ❓ **no anon form (2026-08-01)** | none | high | Still genuine XFS (its `?op=login` page and the family's "Balance / Used space / Traffic available today" header strings are all there, no Cloudflare) — but **probed 2026-08-01 and it renders no anonymous upload form**: neither `/` nor `?op=upload_form` contains a `utype=anon` / `upload.cgi` action, and `?op=api_get_limits` answers with the HTML page instead of the key=value block, so there is no API path either. Account-only at best; needs an account before it can be planned. Same story as the rest of this tier. |
| **Uploady** | uploady.io | 5 GB anon / 10 GB acct | CF-passive | high | Confirmed `op=upload/sess_id/utype`. |
| ~~**Clicknupload**~~ | clicknupload.click | ❌ **account-only** | CF-passive | high | **SHIPPED 2026-07-31 as an ACCOUNT hoster** (ClicknuploadPipeline, web-form path — uploader lives on `?op=my_account.html`, multipart is the family default verbatim). Anonymous is DISABLED (probed 2026-07-31) — an anonymous POST to its own advertised `ServerURL` answers `[{"file_code":"undef","file_status":"uploads are not enabled for your account type"}]`. Its `?op=api_get_limits` is genuine XFS (`MaxUploadFilesize 2048`, `ServerURL https://green01.clicknupload.net/cgi-bin`, empty `SessionID`) and serves no CF challenge, so the ACCOUNT path should be a clean shim. Domain rotates (.click/.org/.co/.vip) — keep the base domain configurable. |
| **Dropgalaxy** | dropgalaxy.com | ~1–2 GB | CF-passive | high | Anon files auto-expire 1 day after last download. |
| ~~**Dailyuploads**~~ | dailyuploads.net | **ANONYMOUS** | CF-passive | high | **SHIPPED 2026-08-02** (DailyUploadsPipeline, on the shared `XfsProAnonymousPipeline`) — anonymous xfspro, verified live with real bytes. `GET /server` → node → `put_chunk.cgi` + `X-Upload-SID` → multipart `api.cgi` with an empty `sess_id`. Its finalise returns **only a `file_code`** (no `links` object), so the link is `dailyuploads.net/<code>`. ⚠ **Its nodes rotate and some are DEAD** — `dn12` answered every PUT with a 500 while `cdn89`/`cdn183` took the same bytes, so the base retries once against a freshly looked-up node. |
| ~~**Easybytez**~~ | easybytez.org | ❌ account-only | CF-passive | — | **SHIPPED 2026-08-03** (`EasybytezPipeline`) — filehoster.io's twin on the wire, so both now share the extracted `XfsProSessionPipeline` (op=start_upload → put_chunk.cgi → **form-urlencoded** import_file). ⚠ Its upload page renders a `utype=anon` guest form and the node still answers *uploads are not enabled for your account type* — the form is decoration. Registered: **200 MB/file, 10 GB storage** (guests 10 MB, premium 7000 MB). Plain username/password, no captcha. The row's "~1–5 GB" was wrong. |
| **UploadBank** | uploadbank.com | 2 GB anon (≤20 files) | CF-passive | med | Clear anon cap; clean shim. |
| ~~**Fileaxa**~~ | fileaxa.com | **ANONYMOUS** | CF-passive | high | **SHIPPED 2026-08-02** (FileaxaPipeline) — **anonymous**, on the XFileSharing **xfspro** chunked plugin (filehoster.io's family), verified live with real bytes through our own client. `GET /server` → `{"url":"https://sNN.fileaxa.com/cgi-bin"}` (keyless) → PUT ≤100 MiB slices to `put_chunk.cgi` with an `X-Upload-SID` header → **multipart** `api.cgi op=import_file` → `{"status":"OK","links":{"download_link":…}}`. **Anonymity is one empty field**: a capture of an anonymous AND a signed-in upload differ ONLY in `sess_id` (empty vs the `xfss` session) — both returned working links, and the anonymous one took a `.avi`. ⚠ **This row was wrong twice, both times from reading the homepage instead of watching it work**: it was first shipped as an account-only REST shim because `/api/*` exists (it does — but the site's own client never uses it, so that upload path was never verified), and "no `utype=anon` form on the homepage" only meant the anonymous uploader is JS-driven. Accounts unbuilt: the account path needs nothing but a real `sess_id`. `MaxFileSize` null — nothing publishes a figure this code can read, and the base's **1 GiB default would silently skip larger files**, the trap to check in every thin shim. |
| **Fastfile** | fastfile.cc | not stated | CF-passive | med | Anon "no account needed"; dynamic `sandbNN.` upload nodes. |
| **Apkadmin** | apkadmin.com | ~1–2 GB | CF-passive | med | APK-sharing XFS. |
| **Drop.download** | drop.download | ~5 GB | CF-passive | med | Ex-DropAPK; pay-per-download, anon expected. |
| ~~**TeraBytez**~~ | terabytez.org | — | DDoS-Guard (passive) | — | **SHIPPED 2026-08-02**, account-only on the web-form path. Three of this row's claims were wrong: not anonymous, not ~5 GB (**100 MB** registered / 5000 MB premium / 10 MB anonymous, per its own plan table), and it is behind DDoS-Guard rather than Cloudflare. It has **no REST API at all** (`/api/upload/server` 404s). ⚠ Files expire 30 days after last download on the registered tier. See `TeraBytezPipeline.cs`. |
| **Uploadboy** | uploadboy.com | not stated | unknown | med | Live since 2012; datacenter IPs blocked (browser UA reaches it). |
| ~~**Elitefile**~~ | elitefile.net | ❌ account-only | CF-passive | — | **SHIPPED 2026-08-03** (`EliteFilePipeline`) — the most stock XFS host in the tree: every route is a family default and its form action already carries `upload_type=file&utype=reg`. No REST API at all (`/api/upload/server` 404s), so sign-in only. ⚠ **Uploads answer `{"domain":"https://elfile.net"}` and the link lives on THAT domain**, not elitefile.net — the base now honours the field. **No per-file cap** (`max_upload_filesize: 0`; the row's "~1–2 GB" was wrong) and **488 GB** storage. |
| **Megaup** | megaup.net | 5 GB (200 GB prem) | unknown | med | Confirm anon vs account. |
| **Fileq** | fileq.net | ≤110 GB adv. | unknown | med | **xfspro/chunked** — start with chunked path. Anon 3-day retention. |
| **Filecat** | filecat.net | unknown | CF-passive | med | **xfspro/chunked** (multithreaded upload). |

### A2 · Account-only (session-cookie / API-key XFS mode)

Wire like the existing session-cookie XFS hosts (Isracloud / Hxfile / Filehoster.io): WebView sign-in → `xfss` cookie / API key → upload.

| Host | Domain | Free cap | Conf. | Note |
|---|---|---|---|---|
| ~~**DDownload**~~ | ddownload.com | no stated cap | high | **SHIPPED 2026-08-01** (DDownloadPipeline) — on the **web-form / session-cookie** path, NOT the API-key one. Its REST API works (verified end to end, upload included) but the key comes only from the **Affiliate Dashboard → Settings** and can't be bootstrapped from `my_account`, so demanding one would gate every user's first upload behind enabling affiliate. Signing in is the shippable flow. Three fork quirks: uploader at `/upload`, dashboard links plain `/logout` (the family signed-in probe wants `?op=logout`), and storage/identity live in redesigned dashboard cards. The "2 GB" once recorded here was unverified — the host publishes NO per-file figure, so `MaxFileSize` is null. |
| **Rosefile** | rosefile.net | 5 GB (80 GB prem) | high | Leech-listed; standard XFS. |
| **Sharemods** ⏸ | sharemods.com | **200 MB** (re-checked 2026-08-08: **still Cloudflare-challenged**, stays disabled) | — | **BUILT then DISABLED 2026-08-02 — and it is ANONYMOUS**, which this row (and every sweep before it) missed by reading the account side only. Its homepage serves the guest form (`utype=anon` + empty `sess_id`); two anonymous uploads were verified with real bytes, both link pages served. ⚠ The page's only `upload.cgi` action is the URL-**importer**'s, so the scrape is rewritten to `upload_type=file`. ⚠⚠ **Cloudflare began challenging our .NET client after ~50 probe requests and had not relented 3 min later, while Python kept getting 200s** — unresolved whether that is permanent or reputation earned by the probing, so the host was left **disabled** rather than shipped on a maybe. The pipeline is complete and tested; re-enable needs only one upload completing from an address that hasn't been probing. See `SharemodsPipeline.cs`. |
| ~~**Userscloud**~~ | userscloud.com | — | high | ❌ **DO NOT BUILD — it IS Send.now, which we already ship.** `userscloud.com/api/upload/server` 301s to `https://send.now/api/upload/server` (which answers with a live send.now node), and its page routes serve the same Cloudflare MANAGED challenge send.now's do. The "same operator family as Usersdrive" note is stale — probed 2026-08-01. |
| ~~**Usersdrive**~~ | usersdrive.com | **5250 MB anon** | high | **SHIPPED 2026-08-01** (UsersDrivePipeline) — and it is **ANONYMOUS**, despite being filed in this account-only tier. Homepage renders `<form id="uploadfile" action="https://dNNN.userdrive.org/cgi-bin/upload.cgi?upload_type=file&utype=anon">` with an empty `sess_id`; a real upload answers `file_status: OK` and the link `usersdrive.com/<code>` serves a live download page. Nodes rotate (d900/d300…), no Cloudflare. Pure config shim on the XFS base. |
| **Modsbase** | modsbase.com | not stated | high | Account-required; mods host. |
| ~~**Uploadrar**~~ | uploadrar.com | 6 GB registered | high | **SHIPPED 2026-08-02** (UploadrarPipeline) — a thin `XFileSharingApiPipeline` shim: its `/api/account/info` and `/api/upload/server` answer the family's usual shapes (a bogus key gets `{"status":400,"msg":"Invalid key"}` from both). Anon confirmed disabled — `?op=api_get_limits` reports `MaxUploadFilesize 0.00001` to a signed-out caller, the DropGalaxy dialect for "not allowed". ⚠ **It BLOCKS mp4/mpg/wmv/mkv/m4v/avi/mp3** (`ExtNotAllowed`) **and only enforces that at the finalise step**: a capture shows a 5 MB `.avi` transferring in full, `put_chunk.cgi` answering OK, and then `import_file` replying `{"error":"unallowed extension"}` — the whole upload spent to earn a refusal. The pipeline now pre-checks the extension locally (new `PreflightRejection` hook on the base). Archive parts are unaffected: the same capture's `.srr` succeeded. Its web UI uses **xfspro** (`start_upload` → `put_chunk.cgi` + `X-Upload-SID` → `import_file`) — the second host seen on that variant after filehoster.io — but the REST API is the simpler route and is what ships; if that ever fails, extract a shared xfspro base rather than a third copy. |
| **Filefox** | filefox.cc | unknown | med | Account-only; WebView → `xfss` → upload. |
| **Rapidrar** | rapidrar.com | ~5 GB | med | Also runs rapidrar.cr mirror. |
| **Worldbytez** | worldbytez.com | multi-GB | med | Connectivity timeout from sandbox; validate from real client. |
| **FastBit** | fastbit.cc | unknown | med | **FlashBit sibling but distinct nginx backend** — may avoid the IIS chunk cap that got FlashBit disabled. Account-only. Verify TLS/cert + chunk behavior. |
| **Datanodes** | datanodes.to | unknown | med | ⚠ Upload restricted to approved webmasters — low real-world value. |
| **Sim File Share** | simfileshare.net | Sims-only | med | ⚠ Invite-only registration, The Sims content only — low value. |

### A3 · Presumed XFS — swept 2026-08-01, **none render an anonymous form**

> ✅ **Swept in full on 2026-08-01 and the result is a clean negative: not one of these serves a
> `utype=anon` / `upload.cgi` upload form.** Six answered normally (filedot.to, filerio.in,
> prefiles.com, xubster.com, hot4share.com, kenfiles.com, salefiles.com, filextras.com) and their
> homepages simply have no anonymous uploader; three were unreachable (filenext.com and
> indishare.org 523, mexa.sh / filesfly.cc / nelion.me no answer). So **the anonymous-XFS seam this
> project mined all session is now genuinely exhausted** — unlike the earlier over-broad claim
> (see the A1 banner), this sweep covered the hosts that had NOT been checked, which is the set
> Usersdrive came out of. Any of these would need an account before it can be planned at all.

~~Filedot (filedot.to)~~ **SHIPPED 2026-08-02 as account-only** — see `FiledotPipeline.cs`. Its
anonymous upload is refused by the host, so the sweep's verdict was right for the wrong reason: it
concluded "no anonymous form" from HTML that has no static form at all.
Filenext (filenext.com), Filerio (filerio.in), Indishare (indishare.org), Prefiles (prefiles.com), Mexashare (mexa.sh), Xubster (xubster.com), Hot4share (hot4share.com), Kenfiles (kenfiles.com), Filesfly (filesfly.cc), Silkfiles (silkfiles.com), Nelion (nelion.me), Filextras (filextras.com), Salefiles (salefiles.com), **Filespace** (filespace.com — *untrusted TLS chain, needs cert-validation relaxation like FlashBit/GigaPeta*).

### A4 · XFS but with an upload-time reCAPTCHA (bumped to Medium)

- **Jumploads** (jumploads.com) — reCAPTCHA on `upload_form`; use the account/session (`xfss`) path to bypass the form captcha. 10 GB max file.

---

## Tier B — Medium effort: new standalone REST pipelines

Bespoke HTTP flows that don't fit XFS or the MoneyPlatform backend. Each needs its own pipeline, but most reuse existing primitives (`UploadPutAsync`, `PostFileChunkAsync`, rotating-node scrape + multipart POST).

| Host | Domain | Anon | Cap | Gating | Effort | Note |
|---|---|---|---|---|---|---|
| **Buzzheavier** | buzzheavier.com | yes | unlimited | none | **Low** | Single raw `PUT` (reuse `UploadPutAsync`). Top pick. |
| ~~**1Fichier**~~ | 1fichier.com | yes | **5 GB** | none | — | **SHIPPED 2026-07-29** (OneFichierPipeline). The ~300 GB recorded here was the PREMIUM figure — the homepage states "300GB for customers, 5GB for guests, 50GB for registered users", so anonymous is 5 GB and an account would raise it to 50 GB. Wire shape as predicted: rotating-node scrape → multipart `file[]` → 302 → result page carries the link. |
| **Krakenfiles** | krakenfiles.com | ⏸ | 1 GB (5 GB prem) | none | **PARKED** | **Anonymous upload is OFF at source (verified 2026-07-30).** A real browser renders `Sorry, service unavailable.` inside the upload box before a file is even chosen, and every one of the 12 `hsN.krakencloud.net` nodes answers the same. Protocol IS solved for whenever it returns: real endpoint is `POST https://hsN.krakencloud.net/_uploader/gallery/upload` (pool of hs1–hs12, picked at random per file) with a single `files[]` multipart field and NO extra fields — **not** the `/api/server/available` + `/api/file` pair, which is a separate route that fails the same way. Response `{"files":[{name,size,error,url,hash,embedUrl}]}`; a per-file `error` string is how failures arrive, inside HTTP 200. Widget states `maxFileSize: 1073741274` and "File size limit: 1 GB". |
| ~~**Sendspace**~~ | sendspace.com | **yes** | **300 MiB** | none | — | **SHIPPED 2026-08-01** (SendspacePipeline) — anonymous, verified with real bytes through our own client (upload, link, and the delete link used to remove it again). Scrape the homepage's single-use ticket (rotating `fsNNu…/upload?…&signature=…` action + the `signature` and `PROGRESS_URL` hidden inputs, ampersands HTML-escaped) → multipart POST with the file under `upload_file[]` and `js_enabled=1`/`terms=1`/empty `file[]` → **the reply IS the result page** (200, no redirect) carrying `sendspace.com/file/<code>` and `sendspace.com/delete/<code>/<hash>`. No cookies: the signature is the whole authorisation. The delete link appears there and nowhere else, so it is logged. Failure is a 301 to `/uploadprocerr.html?e=N`. Cap is 314572800 = **300 MiB exactly**, despite the site saying "300MB". ⚠ **Do not go near the `/dragupload` two-phase XHR path** (`fileField` + `X-File-*` headers → a hash posted back through the form). It exists in `dist/js/homepage.min.js` but is only used for files *dropped* onto the page; a file chosen through Browse submits the form directly. It refuses everything sent from outside a browser (`403 … invalid upload e:2`, `invalid dir e:1`, then 500s) and chasing it cost most of a session before a HAR of a real upload showed the browser posting the plain form all along. **Second lesson from the same detour: every hand-rolled attempt at the plain form failed too — with PowerShell's multipart writer. The identical request through our own browser-shaped writer succeeded first try.** Probe with the real handler before concluding a host refuses you. (Do **not** use the decommissioned `api.sendspace.com/rest/` XML API.) |
| ~~**VikingFile**~~ | vikingfile.com | yes | unlimited | none | — | **SHIPPED 2026-07-30** (VikingFilePipeline). Took the presigned-PUT route: `get-upload-url` → R2 part PUTs + ETag → `complete-upload` with an empty `user`. The doc's `partSize` (1 GiB) is NOT what the live service returns (100 MiB) — read it from the response. Free files are deleted 15 days after last download; `delete-file` needs an account hash. |
| ~~**Webshare**~~ | webshare.cz | **yes** | undeclared | none | — | **SHIPPED 2026-08-01** (WebsharePipeline) — anonymous verified with real bytes, single-shot AND chunked, through our own client. Its uploader is **plupload**, and the field list came out of the site's own script bundle rather than a capture: `POST /api/upload_url/` (keyless, XML) → node, then multipart `file` + `wst`/`folder`/`private`/`adult`/`total`/`offset`/`name` → `{"ident":…}`. **`wst` is the auth token or `''`** — the site's own anonymous path, same shape as VikingFile's empty `user`. Chunked at its own `chunk_size: '1gb'`, first chunk mints the ident, later ones send it back with the running offset. ⚠ **The link is NOT the one the site's JS builds** — `fileLink()` emits `#/file/…`, a client-side route that serves an empty shell to anything without JS; the same path without the `#` is server-rendered, and that's what we emit. The "20 GB" here had no source (the uploader declares `max_file_size: '200gb'`, which is a signed-in client-side guard), so `MaxFileSize` is null. `/api/file_info/` is keyless and confirms a link — but it can 404 for a second or two after an upload while the server assembles. Accounts unbuilt (`/api/salt/` + `/api/login/` → the `wst` token). |
| ~~**Turbobit**~~ | turbobit.net | acct | no stated cap | reCAPTCHA (login) | — | **SHIPPED 2026-08-01** (TurbobitPipeline). HitFile's sibling: WebView sign-in → the page fetches its own `appId` → `POST app.turbobit.net/api/upload/urls {"count":1}` → multipart `Filedata`+`apptype=fd1`+`folder_id=0`+`user_id=appId` → `{"result":true,"id":…}`; link `turbobit.net/<id>.html`. **`apptype` is fd1, NOT HitFile's fd2.** Anonymous left off (guest cap ~200 MB is under one release part). Storage not reported — HitFile's folder-walk would port, and a third sibling would justify a shared base. |
| **Fikper** | fikper.com | — | — | — | **DEAD?** | ⛔ **Both `fikper.com` and `sapi.fikper.com` answer 522 (re-probed 2026-08-01)** — the same Cloudflare origin-timeout the original research saw, still there weeks later. Nothing to build against. Was to be `sapi.fikper.com` + `x-api-key` (NitroFlare-shaped); re-check before ever planning it. |
| **Koofr** | koofr.eu | no | no cap (10 GB storage) | none | Medium | **Cloud drive without OAuth** — HTTP Basic app-password + REST. MediaFire-shaped. |
| **Imgur** | imgur.com | yes | ~20 MB / 200 MB gif | none | Medium | Image-only. v3 API + shipped Client-ID. Catbox-shaped. |
| **ImgBB** | imgbb.com | no (key) | 32 MB | none | Medium | Image-only. Durable API key. |
| **Depositfiles** | depositfiles.com | no | 10 GB | Turnstile (login) | Medium | `GET /api/upload/regular` → multipart w/ `member_passkey`. WebView past Turnstile (NitroFlare pattern). |
| **Cyberdrop** | cyberdrop.me | no | 100–200 MB | CF-passive | **UNREACHABLE** | ⛔ No answer at all on 2026-08-02 (connection fails, not an HTTP status). Was: chibisafe API (`/api/node` → `/api/upload` + token header). Re-check before planning. |
| **Bunkr** | bunkr.cr (rotates) | ❌ | 2 GB | **CF-managed risk** | **BLOCKED — signups closed** | ⛔ **Registration is closed (confirmed 2026-08-02)**, and its upload is account-token based, so there is no way in. Was: chibisafe chunked + account token; aggressive Cloudflare and constant domain rotation (bunkr.si → bunkr.cr). NSFW/media-focused. |
| ~~**DropMeFiles**~~ | dropmefiles.com | **yes** | **50 GB** | none | — | **SHIPPED 2026-08-01** (DropMeFilesPipeline), from its own `js/uploader.js` plus a capture. ⚠ **Links EXPIRE — 14 days is the longest retention offered**, so this is a transfer service, not durable hosting. **The chunk POST is the resumable nginx protocol**: raw slice body plus `Session-ID`, a matching `Content-Disposition: attachment; filename="<session-id>"`, and `Content-Range: bytes s-e/total`. Missing any of those = **415 regardless of content type**, which is what every naive raw POST earns. Intermediate chunks answer `201` + the accumulated range; the last answers `200` + JSON. ⚠ **`save`'s `files[0].id` must be the SAME id used in the chunk `Session-ID` (`<uid>_<fileId>`)** — mismatch fails SILENTLY: every request succeeds, `save` answers "Saved", a link comes back, and the drop page reads *"Files were deleted due to unexpected error while uploading"*. ⚠ **Anti-abuse**: `upload/create` answers `{"error":{"code":99,"message":"Spam"}}` after ~10 calls from one address, and connections start resetting mid-chunk — so uploads are **serialised to 1**. One drop per file (a drop is a folder with one link). Route varies as the site's own `BeforeUpload` picks it: `uploadch` for archive/exe ≤75 MB, `uploadsl` >50 GB, else `uploadrmbl`. **Status: ✅ CONFIRMED — a real upload from the client succeeded 2026-08-02, so the id-pairing fix is proven rather than merely evidenced.** History below. Attempted 2026-08-01; protocol mapped from its own `js/uploader.js` (plupload, like Webshare) but the byte upload was never accepted. **Flow:** `POST /s<SERVERID>/upload/create` (form: `runtime=html5, server=0, updir=, group=, updirType=abc, count, size, period, name, comment`) → `{"result":"<5-char uid>","id":<seconds>}` — **this part works keylessly** — then plupload uploads with **`multipart: false`**, i.e. the RAW chunk as the body to `s<SERVERID>/uploadrmbl?updir=<uid>&name=<file>&chunk=N&chunks=M` (4 MB chunks; the route switches to `uploadsl` above `SPEEDDOWNSIZE`=50 GB and to `uploadch` for archive/executable extensions ≤ `MAXSCANSIZE`=75 MB — a virus-scan path), then `POST /s<SERVERID>/upload/save` (`files` = JSON array of plupload file objects, `uid`, `count`, `size`, `speed`, `period`, …). Link is `dropmefiles.com/<uid>` — **per DROP, not per file**, so one drop per file is needed to fit our one-link-per-file model. `SERVERID` rotates per page load; `period` is 0=1 download, 1=3 days, 2=7 days (default), 3=14 days. **Blocked:** every byte-upload attempt answers `415 Unsupported Media Type` — all three routes, and with `application/octet-stream`, none, `text/plain` and form-encoded. ⚠ **And the host actively flags automation**: after ~10 probes `upload/create` began answering `{"error":{"code":99,"message":"Spam"}}` for the IP. That is a risk for a batch uploader too, not just for probing. **Get a capture of one real upload before spending more** — the same wall Sendspace put up, which a HAR settled in one look. |
| **UploadHaven** | uploadhaven.com | ❌ | — | — | **BLOCKED — paid** | ⛔ **Uploading requires a PAID account (confirmed at the site, 2026-08-02).** That ends it regardless of the protocol: nothing here is reachable to a free or anonymous user. Everything below stays on record only because the bundle told a very different story than the site does — a reminder that a permissive-looking client config says nothing about who is allowed to use it. **Two corrections from its own bundle, 2026-08-02.** (1) **No captcha** — `js/frontend.js` (288 KB) contains no `recaptcha`/`grecaptcha`/`turnstile` reference at all, so the "reCAPTCHA on upload" recorded here is wrong or long stale. (2) Anonymous is **explicitly supported and capped at 5 GB**, in the uploader's own words: `maxFileSize: 5368709120` beside the message *"For unregistered users, the maximum file size is 5 GB."* It is **blueimp jQuery-File-Upload** — the Filestank family, so `PostFileChunkAsync` + `Content-Range` port straight over: `maxChunkSize: 1e8` (100 MB), `method: "POST"`, `sequentialUploads: true`, and `formData` supplying a Laravel `X-CSRF-TOKEN` from the page's `<meta name="csrf-token">`. **What's missing is only the endpoint**: the `#fileupload` form the JS binds to is not in the served HTML on `/` (and `/upload`, `/en`, `/home`, `/files` are all 404), so the uploader is rendered client-side. One browser capture of a real upload would finish this — likely the cheapest remaining host. |
| ~~**Data Vaults**~~ | datavaults.co | no | unlimited | reCAPTCHA (register only) | — | **SHIPPED 2026-08-02** on the API-key path. The row's own doubt was right: it IS re-skinned XFS ("verify it's not" — it is), which made it a shim rather than a Medium. Its published API doc describes exactly the base's flow, and My Account issues keys, so it ships as ApiKey rather than sign-in. Account-only; 5 GB/file; `storage_left: "inf"`. No content restriction was found on the upload path. See `DataVaultsPipeline.cs`. |
| **Subyshare** | subyshare.com | no | premium-oriented | unknown | Medium | ❌ **Not MoneyPlatform — checked 2026-08-02.** `/api/v1/login`, `/api/v2/login` and `/api/v1/getuserinfo` all 404 and the homepage carries none of the family's markers, so the hoped-for "drops to a Low thin subclass of Keep2Share/FileBoom" does not apply. Stays a full Medium build, account-only. |
| **FileFactory** | filefactory.com | ❌ | 1 GB | reCAPTCHA risk | **BLOCKED — needs a recipient** | ⛔ **Its anonymous route is a TRANSFER, not a host: it requires a recipient before it will upload (confirmed at the site, 2026-08-02).** There is no "upload and hand me a link" path, so it cannot serve this app at all — the Convex work below would have been wasted. Alive and un-challenged (re-probed 2026-08-02), and the homepage still references **Convex** plus `/transfer` and an `uploadworker`. Anon `/transfer` → Cloudflare-R2 presigned PUT (would reuse `UploadPutAsync`), **but** orchestrated over an undocumented Convex WebSocket backend — reverse-engineering a reactive WS protocol is a research project, not a shim. Bumped from Medium: of everything left this is the largest unknown per gigabyte gained (1 GB anonymous). |

---

## Tier C — High effort: consumer cloud drives (new OAuth/cloud pattern)

CSUploader has **no** OAuth/cloud-drive pipeline yet. Building the **first** one is the real cost;
subsequent ones get cheaper. None is Cloudflare-blocked — the gate is the auth flow itself.

> ⏸ **DEFERRED 2026-08-02 pending user demand.** Scoped but not built: revisit when someone actually
> asks for a cloud drive. The research below is the part that would otherwise be re-derived, and one
> finding invalidates this section's own premise.
>
> ⛔ **"Consent in an embedded WebView" — the sentence this section used to open with — is IMPOSSIBLE
> for Google.** Google blocks OAuth from embedded webviews with `disallowed_useragent` (enforced
> 2021-09-30, extended to account sign-in 2023-07-24), because an embedding app can inject JS and read
> what the user types. **Our CefGlue sign-in window is exactly what is blocked.** So a cloud drive
> cannot reuse `IInteractiveAuthService` the way all 46 hosters do: consent must open in the user's
> SYSTEM browser, with a loopback listener on `127.0.0.1` catching the `code`. That is a different
> sign-in experience from every other account in the app — a product decision, not just plumbing — and
> it applies equally to Dropbox, OneDrive and pCloud.
>
> ✅ **Scope/verification is NOT the obstacle.** `drive.file` is classified **non-sensitive** and grants
> access only to files the app itself creates — exactly an uploader's needs — so **no verification
> review, no security assessment, no 100-user cap**. Lightweight "brand verification" is needed only to
> put a name and logo on the consent screen. Client type is **Desktop app**; Google's own model treats
> an installed app's client secret as **non-confidential** (it ships in the binary, as rclone and gcloud
> do), so embedding it is the expected practice rather than a compromise.
>
> **The real costs**, in the order they bite: (1) somebody must own a Google Cloud project and appear
> on the consent screen; (2) the system-browser sign-in diverges from the app's established flow;
> (3) storage needs a refresh token — one new persisted field on `FileHosterLoginDto`, which touches
> ~8 sites (see the field checklist); (4) after upload, a link needs `permissions.create`
> (`role=reader`, `type=anyone`) before `webViewLink` is public.
>
> ⚠ **And a fit question worth settling FIRST:** Drive throttles heavily-downloaded public files
> ("download quota exceeded", ~24 h) and public sharing of large archive sets is what gets consumer
> accounts flagged. Drive is personal storage you can share, not a filehost — for an app whose output
> is public links to release parts, that may matter more than any of the engineering above.

| Host | Auth | Free tier | Note |
|---|---|---|---|
| **Google Drive** | OAuth2 (**system browser** + loopback) + resumable upload | 15 GB shared | Scope `drive.file` (non-sensitive → no verification). Desktop-app client, secret ships in the binary. ⛔ Consent CANNOT use the in-app WebView — see the banner. |
| **Dropbox** | OAuth2 + PKCE | 2 GB | **No client secret at all** (PKCE public client), so it's the cleanest first target for the shared plumbing even though 2 GB makes it weak on its own. Single-shot ≤150 MB or chunked `upload_session`; `create_shared_link` for the public URL. |
| **Yandex Disk** | OAuth2 | 1 GB (50 GB paid) | Byte transfer is a raw PUT (reuse `UploadPutAsync`); only the auth layer is novel. SmartCaptcha on login (WebView). |
| **4Shared** | **OAuth 1.0** | 15 GB | Heavier than OAuth2 (HMAC-SHA1 per-request signing + consumer key/secret). Possible cheaper cookie-login alt is unproven. |
| **TeraBox** | proprietary (ndus cookie + jsToken) | 1 TB (~4 GB/file) | Baidu-PCS chunked flow; fragile scraped-token auth. Also a no-login 5 GB "TeraTransfer" (24 h links). |
| **PikPak** | proprietary token + Aliyun OSS | 6 GB | Token/refresh + gcid hashing + OSS multipart signer + a proprietary captcha-token scheme. |
| **123Pan** | OAuth2 (Chinese dev portal) | S3 chunked | Each user needs their own phone-verified Chinese developer credentials — high friction. |

---

## Second candidate list — swept 2026-08-06

A 59-entry list (name + advertised cap/retention). Nine already shipped, nine were already assessed
here. The remaining 41 were swept live: one GET per host for liveness, then family fingerprinting from
the returned markup, then family endpoints for anything promising. Advertised tiers on such lists are
a hypothesis — **UpZur was marked "Sign-Up Required" and uploads anonymously**, which is the same
mistake the earlier UsersDrive entry made.

**Shipped from this sweep**

| Host | Family | Evidence |
|---|---|---|
| **UpZur** (upzur.com) | stock XFS | **SHIPPED 2026-08-06.** `?op=api_get_limits` answers `MaxUploadFilesize 200`, `ServerURL https://systeme.upzur.com/cgi-bin`, empty `ExtNotAllowed`. ⚠ **Its homepage renders NO upload form**, so the base's scrape finds nothing — the node comes from that limits call instead. Anonymous upload verified with real bytes, twice: by hand, then through the shipped pipeline and the real `HttpHandler` (link resolves and the page names the file). See `UpZurPipeline.cs`. |

**Worth doing next, in order**

| Host | Family | Note |
|---|---|---|
| ~~**udrop**~~, ~~**BowFile**~~ | **YetiShare** | **SHIPPED 2026-08-08** on the new shared `YetiSharePipeline` base (Filestank moved onto it too). **Probed 2026-08-07 — both hand a GUEST upload ticket with no login**, which Filestank (the same platform) does not. `GET /assets/js/uploader.js` renders `_sessionid` + `cTracker` + the node URL for an anonymous visitor; a multipart `files[]` POST to that node then answers `[{"name":…,"error":null,"url":"https://www.udrop.com/OY37/<name>","delete_url":…,"short_url":"OY37"}]`. **Verified with real bytes on udrop**: `.rar`, `.r00`, `.sfv` and `.nfo` all accepted as a guest. ⚠ **There IS an extension blocklist** — `.bin` came back *"banned by the site admin"* — so this needs the `RejectedFileExtensionReason` hook, like Uploadrar and filedot. ⚠ **The ticket is SESSION-BOUND**: scraping `uploader.js` without holding the session cookie yields a ticket whose upload 404s, which is the same trap Filestank documents. Guest cap **5 GiB** and **100 MB** chunks, both read from the uploader script (`uploaderMaxSize` / `maxChunkSize`); storage is permanent. The node differs per host — udrop posts to its own apex, BowFile to `fs22.bowfile.com`. Third and fourth hosts on this platform, so per the Turbobit/HitFile precedent doing both means extracting a shared YetiShare base rather than a second bespoke shim (Filestank stays sign-in-only; these two add the guest path). |
| ~~**Filebin**~~ (filebin.net) | bespoke, documented | **SHIPPED 2026-08-08.** The only host here with a **published OpenAPI spec** (`filebin.net/api.yaml`): one `POST /<bin>/<filename>` with the raw body → 201 with the stored file. No cap stated; expiry measured at **7 days** (its blurb says 6). `.rar/.r00/.sfv/.nfo/.zip` all accepted, which matters because the spec documents a 403 for refused types. ⚠ **A bin IS the security model** — anyone with the name sees every file in it — so each upload gets its own 26-hex random bin; the tradeoff was accepted explicitly. It verifies `Content-MD5` when one is sent (400 on mismatch). See `FilebinPipeline.cs`. |
| **GigaFile** (gigafile.nu) | bespoke chunked | Advertised **300 GB / 100 days**, the largest on the list. Japanese UI; homepage references `/upload` and chunking. Worth a real look purely for the cap. |
| ~~**Easyupload.io**~~ | **now a LimeWire frontend — DEAD** | **Rejected 2026-08-08.** Its own dropzone stack is still in the page (`https://upload1.easyupload.io/action.php`, `forceChunking`, 100 MB chunks, and a 10 GB free cap in the JS — not the 4 GB in its title nor the 100 GB the list advertised), **but that node answers 522 to everything** and `easyupload.io/action.php` now 302s to `old.easyupload.io`. The live UI is `js/lw_upload.js`, which imports LimeWire's `file-sharing-lib` and posts to `api.limewire.com`. Same absorption that killed **file.io**. Don't re-investigate unless the native node comes back. |
| ~~**Filego**~~ (filego.io) | bespoke, tiny API | **SHIPPED 2026-08-08** — anonymous, **2 GB**, three calls, protocol read straight out of `/assets/js/bundle.js` (no capture): `POST /api/upload/init` (`name` + a `files` JSON array) → `{id,pw}`; `PUT /api/upload/file/<id>/<index>` with the raw bytes and `X-Filego-Pw`; `POST /api/upload/save` with `expire` — **the link is dead until save lands**. ⚠ **Every reply is HTTP 200, failures included** — the verdict is `status:"ok"\|"error"` in the body, so anything reading the status code reports success for a refused upload. ⚠ Retention is a **1–30 day slider defaulting to 7**; this sends 30 (verified: the returned `expire` came back exactly 30 days out). ⚠ The 2 GB cap is **JS-only** — `init` issues an id for a declared 10 GB, so that call is not evidence the bytes would be accepted. Verified live via the shipped pipeline: `.rar` and `.nfo` both uploaded and downloaded back byte-identical. See `FilegoPipeline.cs`. |
| ~~**UploadNow**~~ | bespoke (Firebase + R2) | **SHIPPED 2026-08-08.** Not xfspro after all — a **Firebase ANONYMOUS** identity authorises `/api/*`, the file is declared to get a bucket config, and the bytes go to **Cloudflare R2 as a signed S3 multipart** where the host runs the signer (the client builds the SigV4 string-to-sign; no secret key). ⚠ The share link is the **FOLDER's** (`/f/<folderId>`), so one folder per file. ⚠ Accounts are **paid-only**, so none are offered. See `UploadNowPipeline.cs`. |
| ~~**UploadHive**~~ | **XFileSharing** | **SHIPPED 2026-08-08** — anonymous, no declared cap. ⚠ **The earlier sweep got this one wrong**: it asked `?op=api_get_limits`, which UploadHive answers with its homepage, so it read as "not XFS". The family form is on **`/upload`**. ⚠ That form has **no `action`** — the page's only `upload.cgi` action is the remote-URL form's, whose `fsNNN.` node is reused with the query rewritten. ⚠ Refuses `.7z` and `.001` (its own `ext_not_allowed`), only after the transfer, so both are rejected up front. See `UploadHivePipeline.cs`. |
| ~~**FileMirage**~~ | bespoke (Laravel + Vue) | **SHIPPED 2026-08-08** — anonymous, **50 GiB**, chunked, and the largest per-file cap of anything here bar GigaFile. The whole protocol came out of **its own JS bundle**, so no capture was needed: `GET /api/servers` (keyless) names a node, then one multipart POST per 99 MB chunk to `<node>/upload.php` carrying `file`, `filename`, `upload_id`, `chunk_number` (0-based) and `total_chunks`; the **last chunk's reply carries the link**. ⚠ The `upload_id` is the **client's to invent** and its own uploader derives it from `Date.now()` — two files started in the same millisecond would share one and be assembled into each other, so this uses random bytes. Verified live at one chunk *and* at two. **Accounts added the same day** from a supplied capture: a signed-in upload is the anonymous one plus `Authorization: Bearer <api_token>`, and the host's own API docs state the trap — *"if not set the file will be uploaded as visitor"*. ⚠ Proven live: a wrong token returns **200 and a working link** while the file lands under no account, and `/api/servers` ignores the header entirely — so **a pasted key could never be validated**. It signs in with email+password instead (plain Laravel form, no captcha) and *derives* the durable token. ⚠ A rejected login is **also a 302**; only the `Location` separates it from a good one. See `FileMiragePipeline.cs`. |
| **FEX.NET**, **DropMB**, **FilePort**, **DooDrive**, **Rootz**, **Bestfile**, **eDisk**, **Imagenetz**, **GrosFichiers**, **Mega4Upload** | mixed | Alive, unclassified, all advertising ≤20 GB, none on a family with a base here. Re-swept 2026-08-08: `fex.net` (mentions reCAPTCHA), `dropmb.com` (Next.js, has a `/upload` route), `fileport.io` and `doodrive.com` (both sign-up-first copy) are the live ones worth a look; `edisk.cz`, `imagenetz.de`, `grosfichiers.com` and `bestfile.io` all redirect off the guessed domain, so **the exact URL is needed before any conclusion**. |
| ~~**megaup.net**~~ | **YetiShare** | **SHIPPED 2026-08-08** as the platform's FOURTH host and its third guest one — anonymous, **5 GiB**, 100 MB chunks, permanent. Found by FINGERPRINT, not from any list: its uploader script declares `uploaderMaxSize = 5368709120` and `maxChunkSize = 100000000`, byte-identical to udrop, which identified the platform before a single byte was sent (the list had it filed only as "5 GB (200 GB prem), confirm anon vs account" — and that 200 GB is STORAGE, not a per-file cap). ⚠ **It declares NO literal `url:`** — it ships a JSON **pool** of nodes plus `getUploadEndpoint()` picking at random, so the base read it as "no upload ticket" until the pool form was added there. Its node is a separate `f1NN.mupload.store` host, so BowFile's cookieless shape. **No extension blocklist** — `.rar`, `.r00`, `.sfv`, `.nfo` and even `.bin` (which udrop refuses) all upload. Verified live at one POST and at 104 MB (multi-chunk). **Accounts verified 2026-08-08** from a supplied capture: family-default login, same pool ticket, and the SAME 5 GiB cap signed in — an account buys the file manager, not a bigger file. ⚠ That capture exposed a base-wide bug: the verifier returned the site's **display name** as `DerivedUsername`, which is written onto the account's `Username` — the identifier the next sign-in posts. "Lynford" cannot log in to the account whose login is "LynfordAudie" (measured), so every refresh quietly broke the account. Fixed for all direct-login hosts on this base (udrop, BowFile, MegaUp). See `MegaUpPipeline.cs`. |
| ~~**fileq.net**~~, ~~**wipfiles.net**~~ | **xfspro chunked — ACCOUNT-ONLY** | **Probed 2026-08-08.** fileq answers `POST op=start_upload` **keylessly** (`{"plugin":"xfspro","url":"https://s99.fileq.net/cgi-bin"}`) and accepts the chunk (`{"status":"OK"}`) — then refuses at the finalise: `{"error":"uploads are not enabled for your account type"}`. wipfiles is the same platform (identical page title and chrome). Same seam as filedot/terabytez/kenfiles/fastfile. |
| ~~**rapidrar.com**~~ (+ `.cr`) | **XFS — unusable** | **Probed 2026-08-08.** `?op=api_get_limits` answers a real XFS envelope, but with **`MaxUploadFilesize 1`** (1 MB for an anonymous session — the DropGalaxy shape, where a nonsensical figure IS the real cap), a **plain-HTTP node IP** (`http://178.162.185.8/cgi-bin`, no TLS) and a homepage that 302s straight to `?op=login`. |
| **kenfiles.com**, **fastfile.cc** | **xfspro — ACCOUNT-ONLY** | **Probed 2026-08-08, both rejected for anonymous.** These were the last two untried hosts from the `GET /server` list, and both answer it *and* accept the chunk (`PUT put_chunk.cgi` → `{"status":"OK"}`) — then refuse at the finalise: `{"error":"uploads are not enabled for your account type"}`. Same verdict as filedot and terabytez, and **the same lesson those two taught: the chunk taking bytes proves nothing, the finalise is the seam.** That closes the `GET /server` set — FILEAXA and DailyUploads are the only anonymous members. Either could still ship as an account host on `XfsProSessionPipeline` if credentials existed. |

**Third sweep — 2026-08-09. Nothing shippable; the reachable anonymous supply is exhausted.**

Everything still open on the lists above was probed to a decisive answer.

| Host | Verdict |
|---|---|
| **YetiShare fingerprint sweep** | `GET /assets/js/uploader.js` against all ten remaining candidates — **no hits**. MegaUp was the last one on that platform. |
| **uploadbank.com** | **A PARKED DOMAIN**, not a host — its "homepage" is an ad-block detector that posts to `router.parklogic.com`. The advertised "2 GB anon" was describing a site that no longer exists. |
| ~~**fastbit.cc**~~ | **REJECTED 2026-08-09 — 1 MB upload cap for REGISTERED users** (user-tested on a real account they registered for this). That is the rapidrar/DropGalaxy outcome: a cap so small the host is useless for anything this app moves. ⚠ Its `?op=my_account` page displays **"1000 Mb"**, which is NOT the upload limit — do not take that figure for one. Everything else was solved and is recorded only in case the cap ever changes: stock XFileSharing with `.html`-suffixed op routes (the Clicknupload fork), account-only (homepage 302s to `?op=login`), signed-in `?op=upload_form` yields `action="https://s3-cloud.online/cgi-bin/upload.cgi?upload_type=file&utype=reg"` plus `sess_id` and the family field set (`add_my_acc`, `link_pass`, `link_rcpt`, `tos`, `token`, `utype`), and **no API key anywhere on my_account**. A second, independent blocker: registration carried a `g-recaptcha-response`, and a headless login POST with a valid `rand`/`token` pair was refused (200, no `xfss`) with the body mentioning both "wrong" and "captcha". |
| **filecat.net**, **filefox.cc**, **doodrive.com**, **fileport.io**, **rootz.io** | None answer `/server`, `op=start_upload` or `?op=api_get_limits`. SPA shells or sign-up-first sites; filecat and filefox also load reCAPTCHA. |
| **fex.net** | reCAPTCHA on the page. |
| ~~**dropmb.com**~~ | **SHIPPED 2026-08-09** from a supplied capture — and it is a **Pingvin Share** instance, which is the useful part: `/api/shares` + `isShareIdAvailable` + a **public keyless `/api/configs`** that states its own limits (`maxSize 512000000`, `chunkSize 10000000`, `maxExpiration "5 years"`, `allowUnauthenticatedShares true`, `shareIdLength 4`). Anonymous or account (`POST /api/auth/signIn` → an `access_token` cookie; no Authorization header anywhere). ⚠ **Every chunk after the first must carry `&id=` the file id chunk 0 returned** — the host does not track slices by share+filename, and the capture was single-chunk so it could not show this; found only by running a real 3-chunk transfer. ⚠ The share id is **client-minted and is the whole security model**; their default length is 4, so this mints 16. ⚠ Retention: its own uploader sends `1-years`, the max is 5 years, and `never` is accepted and verified to serve. See `DropMbPipeline.cs`. |
| **Previously-down hosts, all re-probed** | fikper.com, drop.download, uploadboy.com, desiupload.co (all TCP timeout); cyberdrop.me, worldbytez.com, ranoz.gg, tempcloud.in (no DNS); rosefile.net (521); hexupload.net (expired certificate); mixloads.com (untrusted chain). None have come back. |
| **krakenfiles.com** | ⏸ **Still parked, and now for a second reason.** The site serves HTML again (it was "Sorry, service unavailable" outright), but the homepage still renders that message beside a live `maxFileSize: 1073741274`, **and hs1–hs12 `krakencloud.net` refuse TCP altogether** — so the storage tier is down, not just the widget. The protocol remains solved; re-check the nodes, not the page. |
| **The curl-host category** (0x0.st, uguu, x0.at, envs.sh, oshi.at, bashupload) | Already closed in the sweep above; unchanged. |

**Fourth sweep — 2026-08-09, by WEB SEARCH.** The curated lists were exhausted, so this one went looking for names that were never on them.

| Host | Verdict |
|---|---|
| ~~**Hostize**~~ (www.hostize.com) | **SHIPPED 2026-08-09** — anonymous, keyless, **20 GB**, presigned S3 multipart (third host on the storage.to / VikingFile shape, so the byte path was reused unchanged). ⚠ **Its DOCUMENTED API is not the one to use**: `/api/**v1**/upload/request` answers `401 "Missing API key"` and is Pro-only, while the site's own uploader calls `/api/upload/request` — **no `v1`** — with no key at all. Reading the docs alone would have written this host off. ⚠ `complete` takes only the `shareId`; unlike the other presigned hosts here it needs **no ETags**. ⚠ **Free links live 24 HOURS** and `expiresIn`/`expiry`/`ttl`/`retention` are all accepted and ignored — the trade was accepted explicitly for the 20 GB cap, and the expiry is logged with each link. Note the apex `hostize.com` does not resolve; the host is `www.`. **Accounts deliberately not offered, confirmed by captures 2026-08-09**: sign-in is a **Keycloak OIDC + PKCE** flow (no form to post — the cloud-drive blocker), and a signed-in FREE upload uses the same three endpoints for **the same 24-hour expiry** and the same cap, setting only a `userId`. Longer retention is a paid plan, not an account. See `HostizePipeline.cs`. |
| **Comfyfile** (comfyfile.com) | Sign-up-first ("Create Your Free Account"); its upload page is an authenticated app shell. Not pursued. |
| **Filemail** | Already assessed on an earlier list. |

**Blocked or poor fit from this sweep**

| Host | Reason |
|---|---|
| **TransferSize** | Cloudflare **Turnstile** on the upload page — the pillows.su problem: a token per upload, which nothing here can mint. |
| **Internxt Send** | reCAPTCHA **and** a Cloudflare challenge. |
| **AkiraBox**, **Mega4Upload** | Answer this client the `Just a moment…` **managed** challenge — the TakeFile wall. (Mega4Upload's own pages load; only some paths challenge, so it is listed above too.) |
| **pCloud Transfer**, **JUMBOmail**, **Smash** | Delivery is by **email link**, not a share URL the app can record. |
| **Send** (send.vis.ee) | A Firefox Send fork: E2E, WebSocket upload, and **instance-dependent** — the public instances come and go, so a shipped host would rot. |
| **DesiUpload** (522), **Drop Download** (502 ddos-guard), **Filecad** (525, redirects off-domain) | Not serving. Re-check before investing. |
| **DoraDrop**, **FileFast**, **Gulfup**, **Hexupload**, **MixLoads**, **Ranoz**, **Tempcloud**, **TheUserCloud** | No DNS/TLS answer at the obvious domain from this environment. Either dead or the domain guessed here is wrong — **needs the exact URL before any conclusion**, since a wrong guess proves nothing. |
| **MultiUp.io**, **PolyUploader** | Not hosts. Both are multi-host *uploader front-ends* — the category this app is in, not a target for it. |

**Already shipped** (listed for completeness): 1Fichier, Clicknupload, DataVaults, DropMeFiles, Send.now,
Temp.sh, Transfer.it, ufile.io. **Already assessed above**: Dfiles, FileDitch, FileQ, FileTransfer.io,
KrakenFiles, MegaUp, MixDrop, SwissTransfer, UserDrive.

---

## Blocked / not worth pursuing

| Host | Reason |
|---|---|
| **Fuckingfast** | Cloudflare **managed** challenge observed on the upload API host (`Cf-Mitigated: challenge`, `cType:'managed'`) — same TLS-fingerprint wall as TakeFile/UbiqFile. A guest-route bypass is unverified. Otherwise a healthy S3-presigned host. |
| **Qiwi.gg** | Entire origin (incl. `/api/*`) behind a Cloudflare managed challenge. Blocked. |
| **Pillowcase** (pillows.su) | **Probed live 2026-08-06** — protocol fully mapped from its own SvelteKit bundle, then rejected on two independent grounds. **(1) Per-file Cloudflare Turnstile**, enforced server-side: `POST /api/upload/init` answers `403 {"error":"Invalid captcha response"}` to an empty *or* bogus token, and an `Authorization: Bearer` header changes nothing. The token is consumed per `init`, i.e. **per file** — unlike NitroFlare, where one solve yields a durable reusable hash — so a 16-part release would need 16 human solves. **(2) Audio-only below a paid tier**: anonymous `.mp3/.m4a/.wav/.ogg/.flac/.aif/.aiff` @ 200 MB, a free account adds `.zip` @ 500 MB, and only a **subscription** allows arbitrary files @ 15 GB — so `.rar`/`.r00`/`.sfv` are refused for every unpaid user. ⚠ **The account API key is a red herring**: `/account/apikey` mints one, but no upload route accepts it (`POST /api/upload` 404s, as does every other guessed route) and their own client never sends it — `/docs` reads "In progress". Files are permanent; API base `api.pillows.su` (Fastify). Protocol, for the record: `POST /api/upload/init {session,fileSize,fileName,token}` → `{message:{id}}`; `PUT /api/upload/<id>/part` multipart `part`+`file` (10 MiB chunks, 4 in parallel); `GET /api/upload/<id>/done` → the share id. Revisit only if they publish a key-authenticated upload route. |
| **Swisstransfer** | Google reCAPTCHA enforced on **every** anonymous upload call (not a login) — harvesting a per-upload token headlessly is unproven. Protocol otherwise fine (50 GB). Deprioritize. |
| **iCloud Drive** | No public API/OAuth; only a reverse-engineered private API behind SRP + mandatory HSA2 2FA. Not viable. |
| **Emload** | Cloudflare 403 to all fetches — may be managed (Blocked) or passive; needs a WebView probe. Possibly non-stock XFS. |

---

## Dead / defunct — do not pursue

These appear in the debrid list but are gone (seized, shut down, or parked). Several are **aliases** of live hosts covered above.

| Host | Status |
|---|---|
| **Uptobox** | Seized by French police Sept 2023; resurrection rejected in court. `/api/upload` → 522. |
| **Solidfiles** | Dead ~2 years; DNS fails, 410 Gone. |
| **Files.vc** | Shut down 2025 (API wrapper archived "due to shutdown"). |
| **Anonfiles** / **Bayfiles** | Shut down Aug 2023. |
| **Oboom** | Origin down (522). |
| **Verystream** | Seized by ACE 2019. |
| **Rapidu** | Surrendered to CANAL+ in a piracy settlement; parked. |
| **Fireget** | Connection refused; monitors report down. |
| **Megadb** | Domain unresolvable; was account-only + poor reputation. |
| **File4safe** | Expired/parked on Hostinger. |
| **Uploadbox** | No identifiable live XFS domain; `.io` refused. |
| **DataFileHost** | Dead/repurposed as a file host; was account/FTP-only. |
| **Xvidstage** | Defunct; JDownloader lists it offline. |
| **ddl.to** | Alias → **DDownload** (build that instead). |
| **Tusfiles** | 301 → **Send.cm** (build that instead). |
| **Wayupload** | 301 → **Turbobit** (build that instead). |
| **Sendit.cloud** | Domain null-routed; same platform now lives as **Send.cm**. |
| **dfiles.eu** | Alias of **Depositfiles**. |

---

## Appendix — video-upload hosts (out of chosen scope)

Not in the file-host/cloud scope, but they do accept file uploads. Most are Sibsoft **XVideoSharing** forks (the video sibling of XFS) → thin shims on the same base; a few are bespoke.

| Host | Family | Effort | Note |
|---|---|---|---|
| Vidoza, Mp4upload, Uqload, Darkibox | XVideoSharing / XFS | Low | Thin XFS-base shims (field names differ from file-XFS). Darkibox needs an account api_key. |
| Vup, Cloudvideo, Flix555 | XVideoSharing | Low | Liveness/domain unconfirmed — verify before investing. |
| Mixdrop, Streamtape, Voe | bespoke REST | Medium | Account-only (email + api_key). Voe = 25 GB. |
| Pillowcase | bespoke REST | **Blocked** | Audio-only below a paid tier, **and a per-file Turnstile** — probed live 2026-08-06, see "Blocked / not worth pursuing". |

## Appendix — needs a live browser capture before rating

Reachable evidence was inconclusive from this environment (datacenter-IP blocking); a real-browser capture is needed to confirm protocol + anonymous upload:

**Wushare**, **UniBytes**, **Kshared** (marketed as download-only; UI is a modern cloud-drive, not classic XFS), **Fileland** (fileland.io — ECONNREFUSED), **Filezip** (no confirmable live domain — needs the exact intended domain).

---

## Method note

120 candidate hosts were researched in parallel (one deep pass per flagship/cloud host; batched liveness/family checks for the XFS tail, video hosts, and dead-suspects). Full structured per-host findings — including exact endpoints, source URLs, and confidence — are in the research transcript for this session. Numbers and mechanisms reflect the state on 2026-07-08 and should be re-confirmed against the live node at implementation time.
