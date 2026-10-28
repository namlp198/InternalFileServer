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
- `FileServer:AdminPassword`: admin password used for deleting files and managing folders, change it before deployment

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

Regular users can browse, upload into existing folders and download. Uploads never create folders. Only an admin logged in with `AdminPassword` can delete files and create, rename or delete folders.

## Dashboard

Open `/dashboard.html` (or the `Dashboard` link on the main page). The page shows only a login form until the admin password is entered; no statistics are loaded before login. The login is kept in the browser tab's session storage and shared with the main page.

After login the Dashboard shows:

- Total files, total size and total folders (subfolders below the top-level folders), plus the file count and size of each top-level folder including its subfolders.
- The 10 most recently updated files and the 10 largest files, with name, folder path, size and modified time.
- A `Refresh` button that reloads the statistics and the folder tree.
- The folder tree. Click the arrow to expand or collapse a folder, and click a folder name to list the files directly in that folder.
- Admin actions on the selected folder: create a subfolder, rename, and delete (rename and delete are not offered for `documents` and `tools`). Deleting a folder always asks for confirmation and states how many files and subfolders it contains. Files in the selected folder can be downloaded or deleted after confirmation.

If the admin password stops being valid (for example after it is changed on the server), the next admin request returns 401 and the Dashboard goes back to the login form. Errors from the server are shown with the server's message.

## API

Admin routes need the header `X-Admin-Password`. Every error response is JSON `{ "message": "..." }`: 400 invalid input, 401 missing or wrong admin password, 404 folder or file not found, 409 conflict (duplicate name, folder not empty, file or folder in use), 500 unexpected error.

| Method | Route | Auth | Description |
|---|---|---|---|
| GET | `/api/categories` | - | Top-level folders (`key`, `name`). |
| GET | `/api/folders/tree` | - | Folder tree, one node per top-level folder: `name`, `path`, `fileCount` (direct files), `children`. |
| GET | `/api/files?path=` | - | Files directly in the folder `path`. Each item has `category`, `categoryName`, `name`, `size`, `modified`, `path`. Without `path`, `category=` (or nothing / `all`) lists files in the top-level folders only, as before. |
| POST | `/api/upload` | - | Multipart form with `files` and the target folder in `path` (or the old `category` field). The folder must exist (404 otherwise). Too large files return 400 `File exceeds N MB.`. |
| GET | `/api/download?path=&name=` | - | Downloads a file from any folder. |
| GET | `/api/download/{category}/{filename}` | - | Old route, downloads a file from a top-level folder. |
| DELETE | `/api/files?path=&name=` | admin | Deletes a file from any folder. |
| DELETE | `/api/files/{category}/{filename}` | admin | Old route, deletes a file from a top-level folder. |
| POST | `/api/admin/verify` | - | JSON `{ "password": "..." }`, returns 200 when the password is correct, otherwise 401. |
| GET | `/api/admin/stats` | admin | Totals, per top-level folder totals, `recentFiles` and `largestFiles` (10 each). |
| POST | `/api/folders` | admin | JSON `{ "parentPath": "documents", "name": "Projects" }`, returns 201 with the new folder node. |
| PATCH | `/api/folders` | admin | JSON `{ "path": "documents/Projects", "newName": "Archive" }`, returns 200 with the renamed folder node. Top-level folders return 400. |
| DELETE | `/api/folders?path=&recursive=` | admin | Deletes a folder. A non-empty folder needs `recursive=true` (409 otherwise). Top-level folders and folders containing links return 400. |

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
