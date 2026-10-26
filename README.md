# Mihon Desktop App

An unofficial way to use [Mihon](https://github.com/mihonapp/mihon) on Windows 10 and 11. It runs the
**official Mihon Android app** and installs every new Mihon release automatically.

## How it works

Mihon is written for Android from top to bottom (its UI, database, background jobs and extensions are all
Android code), so it can't simply be rebuilt as a Windows program. `Mihon.exe` does the next best thing:

1. **Android:** it sets up a private Android 17 device with Google's official Android Emulator. The
   emulator runs in the background with its own window hidden, using Windows' built-in hypervisor and your
   graphics card (it falls back to software drawing on PCs where the graphics driver can't be used).
   Android's on-screen keyboard apps are switched off, so the PC keyboard goes straight to Mihon.
   Everything else Mihon doesn't need is switched off too: Google's apps and Play services, Bluetooth,
   sensors and the emulator's unused hardware. That trimmed Android runs in 1.5 GB of memory and stays
   quiet in the background.
2. **Mihon:** it downloads the official `mihon-x86_64` APK from
   [mihonapp/mihon releases](https://github.com/mihonapp/mihon/releases), checks it against the SHA-256
   that GitHub publishes, and installs it.
3. **Window:** it shows Mihon in its own window. Android gives Mihon a screen of its own that is resized
   together with the window, so Mihon switches between its phone and tablet layouts the way a desktop app
   would. The emulator draws that screen with the graphics card and hands every finished frame to the
   window through shared memory, so nothing is video-compressed on the way: scrolling runs at 60 fps on
   about half the CPU that a video stream needs. If that doesn't work on a PC, Mihon is shown with
   [scrcpy](https://github.com/Genymobile/scrcpy) instead, as a compressed video stream.

**Updates:** every time you open Mihon, the launcher asks GitHub for the latest Mihon release. If it's newer
than the one inside Android, it downloads and installs it before the window opens. Your library, history,
extensions and settings are kept, because the update is installed over the existing app. The start after an
update takes about 25 seconds instead of 5: Android starts from scratch, which clears the pictures the old
version left in the emulator's memory.

When you close the window, Android saves its state and shuts down (in about 3 seconds), so it doesn't use
memory while Mihon is closed. The next start takes about 5 seconds, and Mihon is right where you left it.

## Install

1. Download `Mihon.exe` from this fork's [Releases](../../releases/latest).
2. Run it. The file isn't code-signed, so Windows SmartScreen may warn about it: choose
   **More info → Run anyway**.
3. Click **Accept and install**. The first start downloads about 2.5 GB and takes a few minutes
   (Android's first boot alone takes a minute or two).

`Mihon.exe` installs itself to `%LOCALAPPDATA%\Programs\Mihon for Windows` and adds a **Mihon** shortcut to
the Start menu (and, if you leave the box ticked, to the desktop). No administrator rights are needed.

**Requirements:** 64-bit Windows 10 or 11 on an Intel or AMD processor, 8 GB of RAM (Android takes 1.5 GB of it),
about 13 GB of free disk space plus room for downloaded chapters, and hardware virtualization: the
**Windows Hypervisor Platform** feature has to be turned on. If it isn't, Mihon tells you how to turn it on.
A graphics card (integrated is fine) makes scrolling smooth: Android is drawn by the graphics card. On a
16-thread laptop, scrolling a list runs at 60 fps and takes about one of its 16 processors, browsing about a
third of one, and an idle Mihon or a page that's still loading under a tenth.

## Using Mihon on a PC

| To | Do this |
| --- | --- |
| Go back | Right-click, or press the mouse's back button |
| Scroll | Mouse wheel, or drag with the left button as you would with a finger |
| Type | Use the keyboard as usual. Keys type as they would on a US keyboard, whatever Windows layout is active |
| Enter Arabic or other non-Latin text | Paste it with Ctrl+V (typing it directly isn't supported yet) |
| Copy text out of Mihon | Copy in Mihon, then paste on Windows (the clipboards are shared) |
| Full screen | F11 |
| Add a file (a backup, local manga, an extension APK) | Drag it onto the window. APKs are installed; other files go to Android's `Download` folder |
| Quit | Close the window |

On first start, Mihon asks for a storage folder. Choose **Create new folder**, call it `Mihon`, then
**Use this folder** and **Allow**.

Extensions install straight away, without Android asking "Install this app?" for each one: the launcher
sets Mihon to its private extension installer, which keeps extensions inside Mihon (they're marked
**Private** in the extension list). On a phone that prompt is harmless, but here a click in the window while
it opens leaves it hidden behind Mihon, with nothing to bring it back, and extensions stay stuck on
"Installing". To use Android's installer anyway, set `extension_installer=mihon` in the settings.

## Settings

Settings live in `%LOCALAPPDATA%\MihonWindows\settings.ini`. Close Mihon, edit the file in Notepad, and open
Mihon again. Each setting is explained in the file. The useful ones:

- `dpi`: the size of everything in the window (`auto` follows your Windows display scaling).
- `ram_mb`, `cpu_cores`: resources for Android. 1.5 GB and 2 cores are plenty for the trimmed Android. More
  cores (the emulator takes up to 6) don't make it smoother: on a 16-thread laptop, 4 cores scrolled and
  browsed with the same frame times as 2 but took a quarter to a third more CPU. 1 core takes about a third
  less CPU while scrolling than 2, but Mihon starts about half again slower (0.9 s instead of 0.6 s) and
  screens hitch a little more often.
- `animations`: `off` (the default) shows screens and pictures straight away, and loading spinners stand
  still. Every animated frame is drawn in full, so a turning spinner costs as much CPU as fast scrolling (about
  1.3 cores here); without animations a loading page costs no more than an idle one, and moving around Mihon
  takes about a third less. Scrolling is just as smooth either way. `fast` plays animations at double speed,
  `normal` as on a phone.
- `trim_android=false`: switch Android's own apps (Google apps, Play services, Bluetooth) back on. They
  cost about 1 GB of memory and Mihon doesn't use them.
- `view=scrcpy`: show Mihon through scrcpy, as a compressed video stream, the way versions before 1.0.11
  did. It costs about twice the CPU while scrolling. With it, `video_bit_rate` sets the picture quality
  while things move (still pages are sharp either way); going much above the default `8M` makes scrolling
  choppy.
- `keep_running=true`: leave Android running after you close the window, so reopening is instant and
  library updates keep running in the background. Android then keeps using about 1.5 GB of memory.
- `auto_update=false`: stop installing new Mihon releases automatically.
- `extension_installer=mihon`: install extensions with whichever installer is picked in Mihon's own
  settings (**More › Settings › Advanced › Extension installer**) instead of the private one.

## Your data and backups

Your library, downloads and settings live inside the Android device, in `%LOCALAPPDATA%\MihonWindows\avd`.
Each time you close Mihon, its automatic backups are copied to `Documents\Mihon for Windows\Backups`
(when Mihon's storage folder is at the top of Android's storage, like the `Mihon` folder suggested above).
To restore one, drag the `.tachibk` file onto the window and pick it in **More › Backup and restore**.
Mihon's backups also work with Mihon on a phone, in both directions.

## Troubleshooting

- **Logs:** `%LOCALAPPDATA%\MihonWindows\logs`, or **Open logs** in the Mihon Desktop App window.
- **Android won't start:** run `Mihon.exe --cold-boot` to start Android without its saved state.
- **The window shows only Mihon's splash screen:** Android occasionally leaves the splash on top of Mihon. The
  launcher notices within about 15 seconds and clears it, and the next start begins afresh.
- **Start over:** `Mihon.exe --reset-android` deletes the Android device (including your library inside
  it) and sets it up again. Make a backup first.
- **Uninstall:** Settings › Apps › Installed apps › **Mihon Desktop App** › Uninstall. You can choose to keep
  the Android device so a reinstall picks up where you left off.

## What gets downloaded

| Part | From | License |
| --- | --- | --- |
| Android Emulator, platform-tools, Android 17 (Google APIs, x86_64) system image | dl.google.com | [Android SDK License Agreement](https://developer.android.com/studio/terms) |
| scrcpy 5.0.1 | github.com/Genymobile/scrcpy | Apache 2.0 |
| Mihon | github.com/mihonapp/mihon | Apache 2.0 |

Every file is checked against the checksum its source publishes (SHA-1 for Google's files, SHA-256 for
GitHub's) before it's used.

## Releases of this fork

[`.github/workflows/windows.yml`](../.github/workflows/windows.yml) builds `Mihon.exe` and publishes it as a
release named after the Mihon version it was built alongside (for example `windows-v0.21.2`). It runs when
the launcher code changes, and every 6 hours it checks whether Mihon has published a new release. The
launcher doesn't depend on these releases to update Mihon: it always gets Mihon straight from
mihonapp/mihon.

## Building

Needs the .NET SDK (any version from 6 up) on Windows:

```
dotnet build windows/launcher -c Release
```

The output is a single `Mihon.exe` (about 240 KB) that runs on the .NET Framework 4.8 built into Windows.
