// right-hand-backup-background.exe: no window, ever. What the scheduled task runs (`run`); its log is the settings' log_file.
return RightHandBackup.Cli.Run(args, new RightHandBackup.CliHost { Out = TextWriter.Null, Err = TextWriter.Null });
