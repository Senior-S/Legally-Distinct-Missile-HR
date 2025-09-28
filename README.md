# Legally Distinct Missile with hot reload support

> [!NOTE]
> This unturned module requires a custom mono version!  
More info at my [mono fork](https://github.com/Senior-S/mono-appdomain-support/tree/unity-2021.3-mbe).

## Important files
This files contains the changes I made to implement the custom verison of mono  
[**RocketPluginManager.cs**](https://github.com/Senior-S/Legally-Distinct-Missile-HR/blob/master/Rocket/Rocket.Core/Plugins/RocketPluginManager.cs)  
[**Icalls**](https://github.com/Senior-S/Legally-Distinct-Missile-HR/blob/master/Rocket/Rocket.Core/Plugins/Icalls.cs)  
[**RocketPlugin**](https://github.com/Senior-S/Legally-Distinct-Missile-HR/blob/master/Rocket/Rocket.Core/Plugins/RocketPlugin.cs)  
[**Rocket command**](https://github.com/Senior-S/Legally-Distinct-Missile-HR/blob/master/Rocket.Unturned/Commands/CommandRocket.cs#L82)  

## Installation for hot reload

1. Download the latest version of the mono library at  
2. Copy it into `U3DS\MonoBleedingEdge\EmbedRuntime`.
> [!IMPORTANT]  
> Remember to make a backup of **mono-2.0-bdwgc.dll** before replacing it by the custom version!
3. Download the latest module release.
4. Extract the .zip and copy the Rocket.Unturned.HR module folder.
5. Paste it into `U3DS\Modules` folder.