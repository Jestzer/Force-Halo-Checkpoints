# Force Halo Checkpoints
_**You must turn off Easy Anti-Cheat and/or Anti-Cheat before using this program. You will be banned from these games if you fail to do so. If you are using this in a competitive setting, you must disclose so prior to using the program. By using this program, you are entirely responsible for any software, hardware, community, or any other bans you receieve for using this program.**_

Features:
- Forces a checkpoint to occur in the campaign of Windows Halo games. This is useful for practicing mission completion strategies.
- Support all games in the MCC (Steam and Windows Store versions), the original Halo CE for Windows, Halo Custom Edition, Halo 2 Vista with and without the Silent Cartographer mod, and Halo: Campaign Evolved.
- Can bind a keyboard key and controller button/trigger to force a checkpoint, so you don't have to switch to this program to force a checkpoint.

Notes:
- **AI/LLM disclaimer: I used an LLM to help me finish the Linux port of this program. This included making it more consistently poke memory, support a larger variety of controllers, and some UI work.**
- To download and install the program, download the latest release from the right-hand side of the screen. Unzip the contents downloaded and then run the program. The program is portable, so you can put it in any folder you'd like.
- Requires .NET Desktop Runtime 8.0 to be installed. If you don't already have this, you will be told so and prompted to download it.
- Only supports the latest update of MCC.
- Only supports solo gameplay. Does not work in co-op and probably never will unless players sync checkpoints together.
- You need to select which game you want to force a checkpoint in. The first 6 games are for the MCC.
- Halo: Campaign Evolved has only been tested against the Microsoft Store version.
- If you have both the Windows Store and Steam versions of the MCC running, then it's only going to use one of them and there is no option to pick between them at the moment (as I suspect nobody will care about this.) Switching between the 2 should be fine.
- I have only tested this on 2 computers using Windows 11, which may mean you will run into some bugs. Please report them!
- Should support Windows 8.1 and newer. If you get XInput 1.4 working on Windows 7 (or this program at all working on it), then it may work there too. Otherwise, if you're on Windows 7, just don't use the controller button binding.
- If you attempt to run this program in Windows 8.1 without the .NET Desktop Runtime 8.0 installed (or any program requiring this runtime), it may be flagged as a virus.
- Doesn't support Halo Wars nor Halo Spartan Assault/Strike since I don't believe any of those games have checkpoints.
- The Enter and Print Screen buttons cannot be used as hotkeys. I don't intend to add them (except maybe the Enter key, if people really want it.)
- If the hotkeys don't seem to be working, try clicking the checkpoint button. There's a chance you have Halo running as an admin, but not the program to force the checkpoint.

  For folks looking at the source code, the WPF folder is the original, Windows version of this program. I have made a version that also technically works on Linux, but it's a bodge, and haven't had a desire to polish it. It uses the Avalonia framework.


  By the way, if you just want the console command to force a checkpoint in Halo: Custom Edition, it's "game_save_totally_unsafe". You need to enable devmode to use this, or else you'll get an error stating that the command cannot be executed at this time.
