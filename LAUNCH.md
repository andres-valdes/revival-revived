# Running Valheim with BepInEx (Linux)

## Steam Launch Options

Set this in Steam > Valheim > Properties > Launch Options:

```
./run_bepinex.sh %command%
```

## Build & Deploy

```sh
dotnet build           # builds + copies DLL to BepInEx/plugins/ReviveAllies/
dotnet build -c Release  # release build (no auto-deploy)
```

## Verify It Works

After launching Valheim, check:
```
~/.local/share/Steam/steamapps/common/Valheim/BepInEx/LogOutput.log
```

Look for: `[Info   :   BepInEx] Loading [ReviveAllies 0.1.0]`
