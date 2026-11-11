# zBoard plugins

🇬🇧 **English** | 🇩🇪 [Deutsch](PLUGINS.de.md)

zBoard deliberately has **no internet permission**. Features that need the internet (translation, AI rewording,
online dictionaries …) come as **separate plugin apps**, e.g. from the Play Store. Only installing the plugin asks for
internet access – without a plugin, you have a keyboard without internet.

Plugins: **zBoard DeepL** (`plugin-apps/DeepL`) – translates the selected text or the content of the field;
**zBoard Sync** (`plugin-apps/Sync`) – syncs settings and learned data between devices via Google Drive, OneDrive,
Dropbox or WebDAV (HiDrive, Nextcloud …).

## For users

1. Install the plugin (e.g. "zBoard DeepL") and set it up there (API key, target languages).
2. zBoard settings → **Plugins** → turn the plugin on. Before that, it never sees any text.
3. Tap the **puzzle icon** in the bar above the keyboard and choose an action. The plugin receives the selected text,
   or without a selection the whole content of the field (at most 5,000 characters), and replaces it.
   **↶ Undo** restores the original.

zBoard offers no plugins in password fields. Text only goes to a plugin when you explicitly tap.

## For developers: interface (version 1)

A plugin is an Android app with an **exported service**:

```xml
<service android:name=".PluginService" android:exported="true" android:label="My plugin">
  <intent-filter>
    <action android:name="de.World_of_Bytes.zBoard.PLUGIN" />
  </intent-filter>
  <meta-data android:name="de.World_of_Bytes.zBoard.plugin.API_VERSION" android:value="1" />
</service>
```

zBoard binds the service only for the duration of a request and talks to it through a `Messenger`. Each request is a
`Message` with `what`, a `Bundle` as `data` and `replyTo`; the plugin answers with the same `what` to `replyTo` and
returns `requestId` unchanged. Constants: `Plugins/PluginContract.cs` (no Android dependency, can be included directly
in plugin projects).

| `what` | Request (`data`) | Response |
| --- | --- | --- |
| `1` list actions | `requestId`, `apiVersion` | `actionIds` (string[]), `actionLabels` (string[], in the device language) |
| `2` run action | `requestId`, `apiVersion`, `action`, `text`, `language` (e.g. `de`) | `text` (new text) **or** `error` (message for the user) |

Time limits in zBoard: 5 s for connecting and the action list, 20 s for an action. At most 8 actions per plugin.

### Sync plugins

A plugin with the additional meta-data `de.World_of_Bytes.zBoard.plugin.SYNC` = `true` is only a transport to a cloud
storage. It does not appear in the bar; users pick it under Settings → Backup & restore. zBoard provides the file (ZIP,
backup format) through its non-exported content provider `content://de.World_of_Bytes.zBoard.sync/…` and grants the
plugin access only for the duration of one request (`grantUriPermission`). zBoard does the merging itself.

| `what` | Request (`data`) | Reply |
| --- | --- | --- |
| `10` Status | `requestId`, `apiVersion` | `syncReady` (bool), `syncTarget` (label, e.g. "Dropbox") |
| `11` Download | `uri` (write access) | `syncFound` (bool: a file already existed) **or** `error` |
| `12` Upload | `uri` (read access) | empty **or** `error` |

Time limit 90 s per transfer, at most 64 MB. Example: `plugin-apps/Sync` (its README explains the provider setup).

### Security

- **The user decides:** zBoard finds plugins via the intent action but only uses those turned on in the settings.
  When turning one on, zBoard shows whether the plugin uses the internet and whether it comes from the same developer
  (same signature).
- **The plugin checks the caller:** a plugin should only answer the currently active system keyboard
  (compare `Message.sendingUid` → package with `Settings.Secure.DEFAULT_INPUT_METHOD`), so that other apps cannot use
  it. zBoard DeepL does this.
- **No background access:** zBoard sends text only on a tap and never from password fields; before replacing it checks
  that the text in the field is unchanged.

### Example

`plugin-apps/DeepL` – .NET 10 Android app in C#: `PluginService.cs` (service), `DeepLClient.cs` (DeepL API),
`MainActivity.cs` (setup).

```bash
dotnet build plugin-apps/DeepL/ZBoard.DeepL.csproj -t:Run -f net10.0-android
```
