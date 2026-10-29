# Internal File Server

Lightweight .NET 8 web server for internal document and tool/software upload and download.

## Run Locally

```powershell
dotnet run --urls http://0.0.0.0:5088
```

Open on the server machine:

```text
http://localhost:5088
```

Other machines on the LAN can access it with the server IP:

```text
http://<server-ip>:5088
```

## Configuration

Edit `appsettings.json`:

- `FileServer:UploadDirectory`: file storage directory, default is `uploads`
- `FileServer:MaxUploadSizeMB`: max size per file, default is 2048 MB
- `FileServer:UserStoreFile`: account file, default is `data/users.json` (relative to the application folder). It must be outside `wwwroot` and the upload directory, otherwise the server refuses to start.
- `FileServer:MinPasswordLength`: minimum password length, default is `8` (the maximum is 128).
- `FileServer:MaxFailedLogins`: consecutive failed logins before an account is locked, default is `5`.
- `FileServer:LockoutMinutes`: lock duration, default is `15`.
- `FileServer:SessionHours`: sliding session lifetime, default is `8`.

`FileServer:AdminPassword` and the `X-Admin-Password` header no longer exist. If the key is still present in your configuration, the server ignores it and logs a warning. Remove it, and change any old shared password you used elsewhere: earlier versions kept it in `appsettings.json`, so treat it as compromised.

Two optional keys limit the folder tree. They are not in `appsettings.json`; add them under `FileServer` (or set the environment variables `FileServer__MaxFolderDepth` / `FileServer__MaxFolderNameLength`) only when the defaults do not fit:

- `FileServer:MaxFolderDepth`: maximum folder depth, default is `10`. The top-level folder (`documents` or `tools`) counts as level 1.
- `FileServer:MaxFolderNameLength`: maximum length of a new or renamed folder name, default is `100` characters (at most 255).

## Folder Tree

Files are stored under two fixed top-level folders:

- `uploads\documents`
- `uploads\tools`

Admins can create nested subfolders inside them, for example `uploads\documents\Projects\2024`. The disk is the only source of truth: the tree is read from the upload directory on every request, so folders added, renamed or removed with Windows Explorer show up after a refresh. The top-level folders cannot be renamed or deleted, and no new top-level folder can be created.

The API identifies a folder by its path relative to the upload directory, with `/` as separator and the top-level folder as the first segment, for example `documents/Projects/2024`. The first segment is case-insensitive. A path is rejected with 400 when it contains `\`, `:`, control or other invalid file-name characters, starts or ends with `/`, has empty, `.` or `..` segments, has a segment that starts or ends with a space or ends with `.`, or is deeper than `MaxFolderDepth`.

Folder name rules for creating and renaming folders:

- Only letters (including accented letters such as `Tài liệu`), digits, spaces, `.`, `_` and `-`.
- 1 to `MaxFolderNameLength` characters.
- Not only dots, no leading or trailing space, and no trailing `.`.
- Not a Windows device name (`CON`, `PRN`, `AUX`, `NUL`, `COM0`-`COM9`, `LPT0`-`LPT9`, `COM¹`-`COM³`, `LPT¹`-`LPT³`), also not before the first dot (`con.txt` is rejected).
- Unique among the files and folders in the same parent, ignoring case (`Projects` and `projects` conflict, 409).

Existing folders created with Explorer only need to pass the path rules above, so a folder such as `Báo cáo (2024)` can still be browsed and used for uploads.

Signed-in users can browse, upload into existing folders and download. Uploads never create folders. Only an admin can delete files and create, rename or delete folders.

## Accounts and Roles

**Register.** Open `/register.html` (or the `Register` button on the main page). Choose a username (3-32 characters: letters, digits, `.`, `_`, `-`; unique ignoring case) and a password (at least `MinPasswordLength`, at most 128 characters) and confirm it. You are signed in immediately and sent to the File List. New accounts get the role `User`.

**Log in / log out.** Open `/login.html`. A wrong password and an unknown username give the same message. After `MaxFailedLogins` (default 5) consecutive failures the account is locked for `LockoutMinutes` (default 15) and login returns 429 with `Retry-After`, even with the correct password. Lockout state is kept in memory and resets when the server restarts. A disabled account cannot log in. `Log Out` ends all sessions of that user, on every device. Sessions last `SessionHours` (default 8) and slide while in use. The session cookie `ifs.session` is HttpOnly and `SameSite=Lax`.

**Roles and permissions.** Permissions are enforced on the server; hiding buttons in the page is only cosmetic.

| Action | Signed out | User | Admin |
|---|---|---|---|
| Register, log in | yes | - | - |
| Browse folders, list files, upload, download | no | yes | yes |
| Delete files, create/rename/delete folders | no | no | yes |
| Dashboard statistics (`/api/admin/stats`) | no | no | yes |
| List users, change role, enable/disable | no | no | yes |

**First admin (bootstrap).** While no active admin exists, the first account registered from the server machine itself (a loopback connection, for example `http://localhost:5088`) becomes `Admin`. Registration from another machine in that state returns 403 until the first admin exists. Later registrations are `User`; existing passwords are never reset. Warning: behind a reverse proxy running on the same machine every request looks like loopback, so do not expose the server through such a proxy before the first admin has been created. The server has no forwarded-headers handling.

**Managing users.** Admins use the Users card on the Dashboard to change a role or enable/disable an account. A role change applies on the user's next request; disabling ends their session. The last active admin cannot be demoted or disabled (409 `At least one active admin is required.`).

**Account file.** Accounts are stored in `data/users.json` (`FileServer:UserStoreFile`), with PBKDF2-SHA256 password hashes, never plain passwords. The installer creates `{app}\data` as writable. The file is never served, listed or counted. Back it up by copying the file while the server is stopped; to recover, restore the copy. A missing file starts empty; a corrupt file stops the server from starting.

**HTTPS.** On plain HTTP the password and the cookie travel unencrypted on the LAN. Use HTTPS where possible, for example `dotnet run --urls https://0.0.0.0:5443` with a certificate. The cookie gets the `Secure` flag automatically on HTTPS requests (and not on plain HTTP, so LAN login keeps working).

## Dashboard

Open `/dashboard.html` (or the `Dashboard` link, shown to admins only). A signed-out visitor is sent to `login.html?returnUrl=/dashboard.html`. A `User` sees a no-permission card with a link to the File List, and no statistics or user data is requested.

For admins the Dashboard shows:

- Metric cards: total files, total size, total folders (subfolders below the top-level folders), and one card per top-level folder with its file count and size, including subfolders.
- Recently Updated and Largest Files cards (10 each) with name, folder path, size and modified time.
- A `Refresh` button that reloads the statistics, the folder tree and the users.
- The Folders card. Click the arrow to expand or collapse a folder, and click a folder name to list the files directly in that folder. The card header shows the selected path with Rename and Delete (not offered for `documents` and `tools`). Deleting a folder always asks for confirmation and states how many files and subfolders it contains. Files in the selected folder can be downloaded or deleted after confirmation.
- The Users card with username, role, status and created date. Change the role with the dropdown, or use Enable/Disable (Disable asks for confirmation). Server messages, such as the last-admin error, are shown under the card title.

If the session ends (for example the account is disabled), the next request returns 401 and the page goes to the login form. Errors from the server are shown with the server's message.

## API

Every `/api/` route except register, login and logout needs a signed-in session. Unsafe methods (`POST`, `PUT`, `PATCH`, `DELETE`) must send the header `X-Requested-With: XMLHttpRequest` (403 otherwise); the web pages do this automatically. Every error response is JSON `{ "message": "..." }`: 400 invalid input, 401 not signed in, 403 no permission (or missing header), 404 not found, 409 conflict (duplicate name, folder not empty, file or folder in use, last admin), 429 too many attempts, 500 unexpected error.

| Method | Route | Auth | Description |
|---|---|---|---|
| POST | `/api/auth/register` | anonymous | JSON `{ "username", "password", "confirmPassword" }`, returns 201 `{ username, role }` and signs in. 409 when the name is taken. Rate limited. |
| POST | `/api/auth/login` | anonymous | JSON `{ "username", "password" }`, returns 200 `{ username, role }` and signs in. 401 invalid credentials, 403 disabled account, 429 locked. Rate limited. |
| POST | `/api/auth/logout` | anonymous | Ends all sessions of the signed-in user, returns 204. |
| GET | `/api/auth/me` | signed in | `{ username, role }`. |
| GET | `/api/users` | admin | Users: `id`, `username`, `role`, `disabled`, `createdUtc`. |
| PATCH | `/api/users/{id}` | admin | JSON `{ "role": "Admin"\|"User", "disabled": true\|false }` (either field), returns the updated user. 409 when no active admin would remain. |
| GET | `/api/categories` | signed in | Top-level folders (`key`, `name`). |
| GET | `/api/folders/tree` | signed in | Folder tree, one node per top-level folder: `name`, `path`, `fileCount` (direct files), `children`. |
| GET | `/api/files?path=` | signed in | Files directly in the folder `path`. Each item has `category`, `categoryName`, `name`, `size`, `modified`, `path`. Without `path`, `category=` (or nothing / `all`) lists files in the top-level folders only, as before. |
| POST | `/api/upload` | signed in | Multipart form with `files` and the target folder in `path` (or the old `category` field). The folder must exist (404 otherwise). Too large files return 400 `File exceeds N MB.`. |
| GET | `/api/download?path=&name=` | signed in | Downloads a file from any folder. |
| GET | `/api/download/{category}/{filename}` | signed in | Old route, downloads a file from a top-level folder. |
| DELETE | `/api/files?path=&name=` | admin | Deletes a file from any folder. |
| DELETE | `/api/files/{category}/{filename}` | admin | Old route, deletes a file from a top-level folder. |
| GET | `/api/admin/stats` | admin | Totals, per top-level folder totals, `recentFiles` and `largestFiles` (10 each). |
| POST | `/api/folders` | admin | JSON `{ "parentPath": "documents", "name": "Projects" }`, returns 201 with the new folder node. |
| PATCH | `/api/folders` | admin | JSON `{ "path": "documents/Projects", "newName": "Archive" }`, returns 200 with the renamed folder node. Top-level folders return 400. |
| DELETE | `/api/folders?path=&recursive=` | admin | Deletes a folder. A non-empty folder needs `recursive=true` (409 otherwise). Top-level folders and folders containing links return 400. |

Removed: `POST /api/admin/verify` (now 404) and the `X-Admin-Password` header, which is no longer read. Scripts that used them must sign in through `/api/auth/login` and keep the cookie.

## Upgrading

- Anonymous browsing and downloading are over: everyone must register or log in. Register the first admin on the server machine (see above).
- Remove `AdminPassword` from `appsettings.json` and change that password anywhere else it was used.
- Do a hard refresh (Ctrl+F5) after upgrading. A cached old `common.js` does not send the `X-Requested-With` header and its requests fail with 403.
- Re-run the installer (or create the writable `data` folder) so the server can write the account file.

## Limitations

- Recursive folder delete is not atomic. If a file inside is in use, the delete stops partway and returns 409; what was already deleted stays deleted and the rest stays on disk (visible after Refresh). There is no rollback.
- Renaming a folder fails with 409 while a file inside it is open.
- Reparse points (symbolic links and junctions) are hidden from the tree and statistics and are rejected in paths. Folders deeper than `MaxFolderDepth` (for example created with Explorer) are also hidden and cannot be reached through the API. They are not counted in the Dashboard, including the delete confirmation, but are removed when a parent folder is deleted recursively.
- Paths longer than 260 characters work in the server but may cause problems in Windows Explorer, backup tools or archivers. Keep `UploadDirectory`, folder names and the depth short.
- The tree and statistics scan the disk on every request, so very large upload directories make the Dashboard slower.
- Uploading a file with the same name as an existing file in the folder replaces it.

## Publish

```powershell
dotnet publish -c Release
```

The publish output is:

```text
bin\Release\net8.0\publish
```

## Build Setup File

Install Inno Setup 6 on the build machine, then run:

```powershell
scripts\build-setup.bat
```

The setup file is created in:

```text
artifacts\installer
```

The installer includes an optional startup task. When selected, it creates a Windows Startup shortcut that runs `start-server-hidden.vbs`, which starts the server in the background after Windows login.
