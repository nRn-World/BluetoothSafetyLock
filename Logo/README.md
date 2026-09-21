# BluetoothSafetyLock — grafiskt paket

## Innehåll

| Fil | Användning |
|---|---|
| `BluetoothSafetyLock.ico` | Ikon för exe-filen. Tio storlekar: 16, 20, 24, 32, 40, 48, 64, 96, 128, 256 px |
| `BluetoothSafetyLock-shield.ico` | Alternativ med sköld istället för hänglås |
| `logo-horizontal-light.png` | Logotyp för ljus bakgrund, 850 × 280 px |
| `logo-horizontal-dark.png` | Logotyp för mörk bakgrund (vit text), 850 × 280 px |
| `png/icon-16…1024.png` | Ikon som PNG för användning inne i programmet |
| `png/icon-padlock-1024.png` | Master, hänglåsvarianten |
| `png/icon-shield-1024.png` | Master, sköldvarianten |

Samtliga filer har transparent bakgrund.

## 1. Sätt ikonen på exe-filen

Kopiera `BluetoothSafetyLock.ico` till projektmappen och lägg till raden i `BluetoothSafetyLock.csproj`:

```xml
<PropertyGroup>
  <ApplicationIcon>BluetoothSafetyLock.ico</ApplicationIcon>
</PropertyGroup>
```

Ikonen syns i Utforskaren, aktivitetsfältet och Alt+Tab efter nästa `dotnet build`.

## 2. Ikon för fönster och systemfält

Bädda in `.ico`-filen som resurs:

```xml
<ItemGroup>
  <EmbeddedResource Include="BluetoothSafetyLock.ico" />
</ItemGroup>
```

Använd den sedan i `TrayApplicationContext.cs` och `MainDashboard.cs`:

```csharp
using var stream = typeof(Program).Assembly
    .GetManifestResourceStream("BluetoothSafetyLock.BluetoothSafetyLock.ico");

var appIcon = new Icon(stream!);

// Fönsterikon
this.Icon = appIcon;

// Systemfältsikon – välj 16 eller 32 px beroende på DPI
_trayIcon.Icon = new Icon(appIcon, SystemInformation.SmallIconSize);
```

Notera att `NativeMethods.DestroyIcon` redan finns i projektet. Anropa den för ikoner du skapar med `Icon.FromHandle`, annars läcker handtag.

## 3. Logotyp i gränssnittet

`MainDashboard` ritar i dag med mörkt tema, så använd `logo-horizontal-dark.png`. Byt till den ljusa varianten när `NativeMethods.IsWindowsInDarkMode()` returnerar `false`, så följer logotypen systemtemat.

## Färger

| Roll | Hex |
|---|---|
| Primär blå | `#253FE0` |
| Djup indigo | `#1A2AB2` |
| Rubriktext, ljust tema | `#17213D` |
| Accent, mörkt tema | `#96AAFF` |

## Anmärkning om skalning

I 16 px reduceras Bluetooth-runan till en liten markering medan hänglåsets silhuett förblir tydlig. Det är avsiktligt — formen är det som identifierar programmet i små storlekar. Undvik att lägga till fler detaljer i ikonen, eftersom det gör den grumlig i aktivitetsfältet.
