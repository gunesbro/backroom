# Files (cell)

Contract: [docs/contracts/phase-2.md](../../../../docs/contracts/phase-2.md) (flows 6 and 12, "Data", "HTTP APIs", "Privacy rules"). **Human review required** for every change: access checks, signed URLs and storage credentials.

## Responsibility

- Uploads, completion and signed downloads (step 21). Attachments and deletion (22), malware scanning (23), the quota (24), thumbnails and the Files tab (25) and file name search (26) come with their steps.
- `IFileStorage` (Application): the provider interface; `S3FileStorage` (Infrastructure) over any S3-compatible store with the AWS SDK for .NET (SeaweedFS in development and tests, never MinIO). `IFileStorageProvider` chooses the storage per organization; Phase 2 has one per cell.

## Owns

- Schema `files`: `files`, `account_upload_days`, `upload_days` (the upload brakes' daily counters), `outbox` (no events yet; `files.file-deleted.v1` arrives with step 22). Every table has `org_id` and forced RLS.
- The cell's bucket. Keys are `{orgId}/{fileId}`: no name ever appears in a key, URL, log or event.

## Publishes / depends on

- Messaging.Contracts: `IChannelReadAccess` (may the account post in and read the conversation; Phase 1 public channels) and `IMessageFileAccess` (may the account read a message that carries the file; Messaging answers false until step 22).
- Permissions: `files.upload` on upload start (`RequirePermission`).

## Rules

- `POST /api/v1/orgs/{orgId}/files/uploads { conversationId, name, size, contentType }` (`files.upload` and post access): size 1 byte to 100 MB (`413 files.too_large`, `400 files.upload_invalid` below 1), name `FileName` (NFC, trimmed, 1 to 255 code points, no `/` `\`, control or format characters except ZWNJ/ZWJ, not `.`/`..`; `400 files.name_invalid`), content type from `FileTypes` or `application/octet-stream` (no SVG, HTML or parameters; `400 files.type_not_allowed`), unknown or archived conversation `404 files.conversation_not_found`. Writes a `pending` row and answers `201 { fileId, uploadUrl, method: "PUT", headers, expiresAt }`: a presigned PUT valid 15 minutes whose signature covers `Content-Length` (the declared size), `Content-Type` and `If-None-Match: *`, so storage refuses another size or type and any second upload to the key (a completed object can never be replaced through the URL).
- Abuse brakes (`UploadBrakes`, in the start's transaction, before the row is written): per account 30 starts per minute (the newest starts by `files_uploader_created_idx`) and 10 GB per UTC day, per organization 100 GB per UTC day (one counter row per account and day and per organization and day, upserted with a conditional `ON CONFLICT DO UPDATE` whose row lock serializes concurrent starts; account row first, then organization). Declared sizes count at the start; over a limit answers `429 files.rate_limited` with `Retry-After`, and the rollback undoes the counting. Old counter rows are removed by a retention job later (one small row per account and day).
- `POST .../files/{id}/complete` (uploader only, else `404 files.not_found`): no object → `409 files.upload_incomplete` (stays pending); size differs → object deleted, `409 files.upload_incomplete`; the first 512 bytes do not match the declared type (`FileTypes.Confirm`: signature types need their signature, text must have none and no NUL, octet-stream takes the recognised type) → object deleted, `409 files.type_mismatch`. A refused upload stays `pending`, so it can be uploaded again while its URL is valid. Otherwise `ready` with `detected_type` and `kind`; repeats answer the stored file.
- `GET .../files/{id}/download`: access is checked on every request. A `ready` file: only its uploader, while they can still read its conversation; an `attached` one: `IMessageFileAccess`. Every other state, refusal and unknown answer `404 files.not_found`. Success is `302` to a signed GET valid 60 seconds with `response-content-disposition=attachment` and the detected type, plus `Cache-Control: no-store`, `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`; the redirect is written by `SignedUrlRedirect`, never `TypedResults.Redirect` (that logs the URL). A signed URL already handed out stays valid until it expires (at most 60 seconds); revocation applies to the next request.
- Storage settings `Files:Storage` (`S3StorageOptions`, validated at startup): `ServiceUrl`, optional `PublicUrl` (the host clients reach; signatures name it), `Bucket`, `AccessKeyId`, `SecretAccessKey`, `Region`, `Timeout` (5 s per attempt), `MaxRetries` (1); https outside Development; `CreateBucket` only in Development. The SDK sends through `IHttpClientFactory` (`Backroom.Files.Storage`) without the hosts' default resilience handler (the SDK retries itself), with checksums only where required. Completion holds the unit of work's connection during its storage calls, so they share a 15-second budget (`CompletionStorageBudget`); a slow store fails the request instead of starving the pool.
- Only this module's Infrastructure may use an object storage SDK (architecture test `ObjectStorageUsageTests`).

## Status

- Implemented (Phase 2 step 21, behind `Features:Files`, app role only): schema `files` (migration `20261124150000_CreateFiles`), upload start, completion and download, `S3FileStorage`, the AppHost's SeaweedFS with generated secret credentials and one bucket per cell (`backroom-cell-{id}`).
- Not yet: the quota reservation (step 24 plugs in before the row is written in `FileService.StartAsync`), the 24-hour expiry of unattached uploads, browser CORS on the store for direct uploads from the web app, `X-Content-Type-Options` on the store's own responses (deploy-phase store config or proxy), one storage identity per bucket in deployments (development shares one SeaweedFS identity between the cells; the deploy checklist must require one per bucket), and a file name on downloads (the URL must not carry it; the client names the saved file).
