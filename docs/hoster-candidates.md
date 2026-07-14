# File-Hoster Expansion Candidates

_Derived from the [fynks/debrid-services-comparison](https://github.com/fynks/debrid-services-comparison) host list (328 hosts supported across 9 debrid services), assessed 2026-07-08._

This document outlines file-hosting sites CSUploader **could** add upload support for, based on the hosts that debrid services support. Each candidate was researched for whether it is alive and upload-capable today, its real upload mechanism, anonymous-vs-account requirement, captcha / Cloudflare gating, per-file limit, and how it maps onto CSUploader's existing pipeline architecture.

## How to read this

**Scope.** The debrid list mixes true upload hosts with download-only platforms. This document covers **file-upload hosts + consumer cloud storage** only. Download-only streaming (YouTube, Vimeo, Twitch…), social (Twitter, Reddit, Instagram…) and news sites were dropped — you can't upload an arbitrary file to them. Pure video-upload hosts are in an appendix (out of the chosen scope but technically upload-capable).

**Already excluded.** The 27 hosters CSUploader already ships and the 5 disabled-in-tree (ExtMatrix, FlashBit, Hotlink, TakeFile, UploadGIG) are not repeated here.

**Effort legend** (based on CSUploader's existing bases):

| Effort | Meaning |
|---|---|
| **Low** | Thin shim on `XFileSharingApiPipeline` (XFS family) or a trivial reuse of an existing primitive |
| **Medium** | New standalone REST pipeline (bespoke HTTP flow, like MediaFire / Pixeldrain / Gofile / Storage.to) |
| **High** | New OAuth / cloud-drive pattern — CSUploader has **none** yet (WebView consent + refresh-token storage) |
| **Blocked** | Cloudflare **managed** challenge (TLS-fingerprinted — proven dead-end, cf. TakeFile/UbiqFile), per-upload captcha, premium-only upload, or the service is dead |

**Confidence caveat.** Most mechanisms were corroborated from live probes + multiple open-source uploader implementations, but few had a full live round-trip from this environment (datacenter IPs are frequently bot-blocked). Before building any host, verify **anon-vs-account, exact per-file cap, and passive-vs-managed Cloudflare** against the live upload node. XFS clones especially: a "Low shim" assumes the host is genuinely stock XFileSharing and its upload node is not behind a managed challenge — confirm both.

---

## Recommended shortlist

The best first adds, ranked by value ÷ effort. All are alive, anonymous (no login needed), and ungated:

| # | Host | Effort | Why |
|---|---|---|---|
| 1 | **Buzzheavier** | Low | Simplest possible pipeline: a single raw `PUT` of the file body reusing `HttpHandler.UploadPutAsync`, parse the JSON `id`. Anonymous, no cap, no captcha, no managed Cloudflare. |
| 2 | **Send.cm** | Low | Classic XFS host — **anonymous upload verified live 2026-07-08** (`/api/upload/server` returned a keyless upload node). Maps 1:1 onto `XFileSharingApiPipeline`. ~20 GB free. |
| 3 | **Filestank** | Low | Stock XFS, anonymous drag-and-drop, **20 GB** free per file, no Cloudflare, files kept indefinitely. Cleanest XFS batch pick. |
| 4 | **1Fichier** | Medium | Major, well-known host. Anonymous web-form upload, **~300 GB/file**, no upload-side captcha. Standalone pipeline shaped like the existing Upstore/GigaPeta flow. |
| 5 | **Krakenfiles** | Medium | Anonymous 1 GB, no gating. `GET /api/server/available` → single multipart POST → parse `data.url`. Gofile/Upstore-shaped standalone. |
| 6 | **Sendspace** | Medium | Well-known brand. Anonymous 300 MB, no captcha, plain nginx. Upstore-shaped web-form standalone. (Do **not** use the decommissioned `api.sendspace.com/rest/` XML API.) |
| 7 | **VikingFile** | Medium | Documented public API, true anonymous, no captcha, effectively unlimited. Reuses Storage.to/Gofile primitives. |
| 8 | **Uploady** / **WIP Files** | Low | Confirmed stock XFS, anonymous (Uploady 5 GB / WIP Files no-Cloudflare). Direct XFS-base shims. |
| 9 | **Turbobit** | Medium | Big brand and the same-operator sibling of the already-shipped **HitFile** — its upload wire shape is nearly identical, so it's a low-risk copy. Anonymous 200 MB (100 GB registered). |
| 10 | **Koofr** | Medium | The one cloud drive that is **not** OAuth — HTTP Basic app-password + plain REST, models cleanly on the MediaFire/Pixeldrain account pipelines. 10 GB free. |

If image hosting is ever in scope, **Imgur** (anonymous via a shipped Client-ID) is a small Medium add.

---

## Tier A — Low effort: XFileSharing (XFS) shims

All map onto `XFileSharingApiPipeline` (classic variant unless noted). A new host is a thin shim; the risk is per-host liveness / anonymous-availability / passive-vs-managed Cloudflare, to confirm at build time.

### A1 · Anonymous-capable, low/no gating (best value)

| Host | Domain | Free cap | Gating | Conf. | Note |
|---|---|---|---|---|---|
| **Send.cm** | send.cm (→ send.now) | ~20 GB | CF-passive | high | Anon verified live 2026-07-08. tusfiles/file.cm/sendit lineage. |
| **Filestank** | filestank.com | 20 GB | none | high | Anon drag-drop, kept "forever", public API. |
| **WIP Files** | wipfiles.net | XFS default | none | high | Genuine Sibsoft XFS, no Cloudflare. |
| **Uploady** | uploady.io | 5 GB anon / 10 GB acct | CF-passive | high | Confirmed `op=upload/sess_id/utype`. |
| **Clicknupload** | clicknupload.click | 2 GB guest | CF-passive | high | Domain rotates (.click/.org/.co/.vip) — make base domain configurable. |
| **Dropgalaxy** | dropgalaxy.com | ~1–2 GB | CF-passive | high | Anon files auto-expire 1 day after last download. |
| **Dailyuploads** | dailyuploads.net | ~1–5 GB | CF-passive | high | Actively maintained (domain to 2027). |
| **Easybytez** | easybytez**.org** | ~1–5 GB | CF-passive | high | Reference XFS host. Target `.org` (`.com` refused). |
| **UploadBank** | uploadbank.com | 2 GB anon (≤20 files) | CF-passive | med | Clear anon cap; clean shim. |
| **Fileaxa** | fileaxa.com | ~10 GB | CF-passive | med | Free upload to 10000 MB; documented cURL API (possible api-key path). |
| **Fastfile** | fastfile.cc | not stated | CF-passive | med | Anon "no account needed"; dynamic `sandbNN.` upload nodes. |
| **Apkadmin** | apkadmin.com | ~1–2 GB | CF-passive | med | APK-sharing XFS. |
| **Drop.download** | drop.download | ~5 GB | CF-passive | med | Ex-DropAPK; pay-per-download, anon expected. |
| **TeraBytez** | terabytez.org | ~5 GB | CF-passive | med | Pay-per-download XFS. |
| **Uploadboy** | uploadboy.com | not stated | unknown | med | Live since 2012; datacenter IPs blocked (browser UA reaches it). |
| **Elitefile** | elitefile.net | ~1–2 GB | CF-passive | med | Anon reportedly permitted. |
| **Megaup** | megaup.net | 5 GB (200 GB prem) | unknown | med | Confirm anon vs account. |
| **Fileq** | fileq.net | ≤110 GB adv. | unknown | med | **xfspro/chunked** — start with chunked path. Anon 3-day retention. |
| **Filecat** | filecat.net | unknown | CF-passive | med | **xfspro/chunked** (multithreaded upload). |

### A2 · Account-only (session-cookie / API-key XFS mode)

Wire like the existing session-cookie XFS hosts (Isracloud / Hxfile / Filehoster.io): WebView sign-in → `xfss` cookie / API key → upload.

| Host | Domain | Free cap | Conf. | Note |
|---|---|---|---|---|
| **DDownload** | ddownload.com | 2 GB | high | Clean XFS **API-key** path (`api-v2.ddownload.com/api/upload/server?key=`). Strong. Ex-ddl.to. |
| **Rosefile** | rosefile.net | 5 GB (80 GB prem) | high | Leech-listed; standard XFS. |
| **Sharemods** | sharemods.com | multi-GB | high | 13-yr host; `op=upload_form` confirmed. |
| **Userscloud** | userscloud.com | "unlimited" | high | Same operator family as Usersdrive. |
| **Usersdrive** | usersdrive.com | multi-GB | high | Sibling of Userscloud. |
| **Modsbase** | modsbase.com | not stated | high | Account-required; mods host. |
| **Uploadrar** | uploadrar.com | 6 GB registered | high | Anon disabled (~0 cap). Account only. |
| **Filefox** | filefox.cc | unknown | med | Account-only; WebView → `xfss` → upload. |
| **Rapidrar** | rapidrar.com | ~5 GB | med | Also runs rapidrar.cr mirror. |
| **Worldbytez** | worldbytez.com | multi-GB | med | Connectivity timeout from sandbox; validate from real client. |
| **FastBit** | fastbit.cc | unknown | med | **FlashBit sibling but distinct nginx backend** — may avoid the IIS chunk cap that got FlashBit disabled. Account-only. Verify TLS/cert + chunk behavior. |
| **Datanodes** | datanodes.to | unknown | med | ⚠ Upload restricted to approved webmasters — low real-world value. |
| **Sim File Share** | simfileshare.net | Sims-only | med | ⚠ Invite-only registration, The Sims content only — low value. |

### A3 · Presumed XFS, needs a live browser capture first

Confirmed alive and XFS-shaped, but anon-vs-account, cap, and/or gating unverified (fetches were bot-blocked or transiently errored):

Filedot (filedot.to), Filenext (filenext.com), Filerio (filerio.in), Indishare (indishare.org), Prefiles (prefiles.com), Mexashare (mexa.sh), Xubster (xubster.com), Hot4share (hot4share.com), Kenfiles (kenfiles.com), Filesfly (filesfly.cc), Silkfiles (silkfiles.com), Nelion (nelion.me), Filextras (filextras.com), Salefiles (salefiles.com), **Filespace** (filespace.com — *untrusted TLS chain, needs cert-validation relaxation like FlashBit/GigaPeta*).

### A4 · XFS but with an upload-time reCAPTCHA (bumped to Medium)

- **Jumploads** (jumploads.com) — reCAPTCHA on `upload_form`; use the account/session (`xfss`) path to bypass the form captcha. 10 GB max file.

---

## Tier B — Medium effort: new standalone REST pipelines

Bespoke HTTP flows that don't fit XFS or the MoneyPlatform backend. Each needs its own pipeline, but most reuse existing primitives (`UploadPutAsync`, `PostFileChunkAsync`, rotating-node scrape + multipart POST).

| Host | Domain | Anon | Cap | Gating | Effort | Note |
|---|---|---|---|---|---|---|
| **Buzzheavier** | buzzheavier.com | yes | unlimited | none | **Low** | Single raw `PUT` (reuse `UploadPutAsync`). Top pick. |
| **1Fichier** | 1fichier.com | yes | ~300 GB | none | Medium | Rotating-node scrape → multipart POST → regex link. Upstore/GigaPeta-shaped. Account/API path is an easy later add. |
| **Krakenfiles** | krakenfiles.com | yes | 1 GB (2 GB acct) | none | Medium | `GET /api/server/available` → multipart POST. Gofile-shaped. |
| **Sendspace** | sendspace.com | yes | 300 MB | none | Medium | Scrape rotating form action → single-file POST. Upstore-shaped. |
| **VikingFile** | vikingfile.com | yes | unlimited | none | Medium | Documented API; get-server single POST **or** presigned-PUT+ETag (Storage.to-shaped). |
| **Webshare** | webshare.cz | yes | 20 GB | none | Medium | Get upload_url → multipart POST name+data → parse `ident`. Upstore-shaped. |
| **Turbobit** | turbobit.net | yes | 200 MB (100 GB acct) | reCAPTCHA (login) | Medium | Near-identical to the shipped HitFile pipeline. Login captcha via WebView. |
| **Fikper** | fikper.com | yes | ~1 GB (10 GB+ prem) | CF-passive | Medium | `sapi.fikper.com` + `x-api-key` (NitroFlare-shaped). ⚠ Infra was 522-timing-out during research; verify live. |
| **Koofr** | koofr.eu | no | no cap (10 GB storage) | none | Medium | **Cloud drive without OAuth** — HTTP Basic app-password + REST. MediaFire-shaped. |
| **Imgur** | imgur.com | yes | ~20 MB / 200 MB gif | none | Medium | Image-only. v3 API + shipped Client-ID. Catbox-shaped. |
| **ImgBB** | imgbb.com | no (key) | 32 MB | none | Medium | Image-only. Durable API key. |
| **Depositfiles** | depositfiles.com | no | 10 GB | Turnstile (login) | Medium | `GET /api/upload/regular` → multipart w/ `member_passkey`. WebView past Turnstile (NitroFlare pattern). |
| **Cyberdrop** | cyberdrop.me | no | 100–200 MB | CF-passive | Medium | chibisafe API (`/api/node` → `/api/upload` + token header). ⚠ Risk: managed CF on upload node would flip to Blocked. |
| **Bunkr** | bunkr.si (rotates) | ? | 2 GB | **CF-managed risk** | Medium | chibisafe chunked + account token. ⚠ Aggressive Cloudflare + constant domain rotation; managed challenge on `/api/node` would flip to Blocked. NSFW/media-focused. |
| **DropMeFiles** | dropmefiles.com | yes | 100 GB | none | Medium | ⚠ Temporary links (25-day expiry) — transfer service, not durable hosting. |
| **UploadHaven** | uploadhaven.com | ? | 100 GB (acct) | **reCAPTCHA on upload** | Medium | ⚠ Captcha on the upload action itself (not just login) — real risk; only viable if a non-captcha/account path exists. |
| **Data Vaults** | datavaults.co | no | unlimited (games/sw only) | unknown | Medium | Own API doc; content-restricted. Low confidence — verify it's not re-skinned XFS. |
| **Subyshare** | subyshare.com | no | premium-oriented | unknown | Medium | ⚠ Check if it's on the MoneyPlatform `/v1` backend first — if so it drops to a **Low** thin subclass. |
| **FileFactory** | filefactory.com | yes | 1 GB | reCAPTCHA risk | Medium | Anon `/transfer` → Cloudflare-R2 presigned PUT (reuse `UploadPutAsync`), **but** orchestrated via an undocumented Convex WS backend + reCAPTCHA Enterprise. Capture one real transfer before committing; the per-upload reCAPTCHA could push it to High. |

---

## Tier C — High effort: consumer cloud drives (new OAuth/cloud pattern)

CSUploader has **no** OAuth/cloud-drive pipeline yet. Each of these needs the brand-new pattern: OAuth2 (or worse) consent in an embedded WebView + refresh-token storage. Building the **first** one is the real cost; subsequent ones get cheaper. None is Cloudflare-blocked — the gate is the auth flow itself.

| Host | Auth | Free tier | Note |
|---|---|---|---|
| **Google Drive** | OAuth2 + resumable upload | 15 GB shared | Cleanest OAuth2 target; registered Google Cloud client needed. Best "first cloud drive" candidate. |
| **Dropbox** | OAuth2 + PKCE | 2 GB | Single-shot ≤150 MB or chunked `upload_session`; `create_shared_link` for the public URL. |
| **Yandex Disk** | OAuth2 | 1 GB (50 GB paid) | Byte transfer is a raw PUT (reuse `UploadPutAsync`); only the auth layer is novel. SmartCaptcha on login (WebView). |
| **4Shared** | **OAuth 1.0** | 15 GB | Heavier than OAuth2 (HMAC-SHA1 per-request signing + consumer key/secret). Possible cheaper cookie-login alt is unproven. |
| **TeraBox** | proprietary (ndus cookie + jsToken) | 1 TB (~4 GB/file) | Baidu-PCS chunked flow; fragile scraped-token auth. Also a no-login 5 GB "TeraTransfer" (24 h links). |
| **PikPak** | proprietary token + Aliyun OSS | 6 GB | Token/refresh + gcid hashing + OSS multipart signer + a proprietary captcha-token scheme. |
| **123Pan** | OAuth2 (Chinese dev portal) | S3 chunked | Each user needs their own phone-verified Chinese developer credentials — high friction. |

---

## Blocked / not worth pursuing

| Host | Reason |
|---|---|
| **Fuckingfast** | Cloudflare **managed** challenge observed on the upload API host (`Cf-Mitigated: challenge`, `cType:'managed'`) — same TLS-fingerprint wall as TakeFile/UbiqFile. A guest-route bypass is unverified. Otherwise a healthy S3-presigned host. |
| **Qiwi.gg** | Entire origin (incl. `/api/*`) behind a Cloudflare managed challenge. Blocked. |
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
| Pillowcase | bespoke REST | Medium | **Audio-only** — poor fit for a general uploader. |

## Appendix — needs a live browser capture before rating

Reachable evidence was inconclusive from this environment (datacenter-IP blocking); a real-browser capture is needed to confirm protocol + anonymous upload:

**Wushare**, **UniBytes**, **Kshared** (marketed as download-only; UI is a modern cloud-drive, not classic XFS), **Fileland** (fileland.io — ECONNREFUSED), **Filezip** (no confirmable live domain — needs the exact intended domain).

---

## Method note

120 candidate hosts were researched in parallel (one deep pass per flagship/cloud host; batched liveness/family checks for the XFS tail, video hosts, and dead-suspects). Full structured per-host findings — including exact endpoints, source URLs, and confidence — are in the research transcript for this session. Numbers and mechanisms reflect the state on 2026-07-08 and should be re-confirmed against the live node at implementation time.
