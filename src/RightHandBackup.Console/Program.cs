// right-hand-backup.exe. Output is Unicode to a console and UTF-8 when redirected (see ConsoleText).
return RightHandBackup.Cli.Run(args, new RightHandBackup.CliHost { Out = WinBackground.ConsoleText.Out, Err = WinBackground.ConsoleText.Error });
