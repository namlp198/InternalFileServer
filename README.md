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
- `FileServer:AdminPassword`: admin password used for deleting files, change it before deployment

Files are stored in two category folders:

- `uploads\documents`
- `uploads\tools`

Regular users can upload and download. Only an admin logged in with `AdminPassword` can delete files.

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
