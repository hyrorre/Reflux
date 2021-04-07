﻿using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Globalization;

namespace infinitas_statfetcher
{
    class Program
    {
        [DllImport("kernel32.dll")]
        public static extern bool ReadProcessMemory(int hProcess,
            Int64 lpBaseAddress, byte[] lpBuffer, int dwSize, ref int lpNumberOfBytesRead);
        /* Import external methods */
        [DllImport("kernel32.dll")]
        public static extern IntPtr OpenProcess(int dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        static void Main()
        {
            Process process = null;
            Config.Parse("config.ini");

            Dictionary<string, Tuple<int, int>> unlock_db = new Dictionary<string, Tuple<int, int>>();
            bool init = false;
            try
            {
                foreach (var line in File.ReadAllLines("unlockdb"))
                {
                    var s = line.Split(',');
                    unlock_db.Add(s[0], new Tuple<int, int>(int.Parse(s[1]), int.Parse(s[2])));
                }
            } catch (FileNotFoundException e)
            {
                init = true;
                if (Config.Save_remote)
                {
                    Console.WriteLine("unlockdb not found, will initialize all songs on remote server");
                }
            }
            DirectoryInfo sessionDir = new DirectoryInfo("sessions");
            if (!sessionDir.Exists)
            {
                sessionDir.Create();
            }
            DateTime now = DateTime.Now;

            CultureInfo culture = CultureInfo.CreateSpecificCulture("en-us");
            DateTimeFormatInfo dtformat = culture.DateTimeFormat;
            dtformat.TimeSeparator = "_";
            var sessionFile = new FileInfo(Path.Combine(sessionDir.FullName, $"Session_{now:yyyy_MM_dd_hh_mm_ss}.tsv"));

            Console.WriteLine("Trying to hook to INFINITAS...");
            do
            {
                var processes = Process.GetProcessesByName("bm2dx");
                if (processes.Any())
                {
                    process = processes[0];
                }

                Thread.Sleep(2000);
            } while (process == null);

            Console.Clear();
            Console.WriteLine("Hooked to process, waiting until song list is loaded...");


            IntPtr processHandle = OpenProcess(0x0410, false, process.Id); /* Open process for memory read */
            Utils.handle = processHandle;

            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            var bm2dxModule = process.MainModule;
            Offsets.LoadOffsets("offsets.txt");

            Utils.Debug($"Baseaddr is {bm2dxModule.BaseAddress.ToString("X")}");
            byte[] buffer = new byte[80000000]; /* 80MB */
            int nRead = 0;
            ReadProcessMemory((int)processHandle, (long)bm2dxModule.BaseAddress, buffer, buffer.Length, ref nRead);
            string versionSearch = "P2D:J:B:A:";
            var str = Encoding.GetEncoding("Shift-JIS").GetString(buffer);
            bool correctVersion = false;
            string foundVersion = "";
            for (int i = 0; i < str.Length - Offsets.Version.Length; i++)
            {

                if (str.Substring(i, versionSearch.Length) == versionSearch)
                {
                    foundVersion = str.Substring(i, Offsets.Version.Length);
                    Utils.Debug($"Found version {foundVersion} at address +0x{i:X}");
                    /* Don't break, first two versions appearing are referring to 2016-builds, actual version appears later */
                }
            }
            if (foundVersion != Offsets.Version)
            {
                if (Config.UpdateFiles)
                {
                    Console.WriteLine($"Version in binary ({foundVersion}) don't match offset file ({Offsets.Version}), querying server for correct version");
                    correctVersion = Network.UpdateOffset(foundVersion);
                } else
                {
                    Console.WriteLine($"Version in binary ({foundVersion}) don't match offset file ({Offsets.Version})");
                }
                Network.UpdateEncodingFixes();
            } else
            {
                correctVersion = true;
            }
            Utils.LoadEncodingFixes();

            if (!correctVersion)
            {
                Console.WriteLine("Application will now exit");
                Console.ReadLine();
                return;
            }

            bool songlistFetched = false;
            while (!songlistFetched)
            {
                while (!Utils.SongListAvailable()) { Thread.Sleep(5000); } /* Don't fetch song list until it seems loaded */
                Thread.Sleep(1000); /* Extra sleep just to avoid potentially undiscovered race conditions */
                Utils.FetchSongDataBase();
                if (Utils.songDb["80003"].totalNotes[3] < 10) /* If Clione (Ryu* Remix) SPH has less than 10 notes, the songlist probably wasn't completely populated when we fetched it. That memory space generally tends to hold 0, 2 or 3, depending on which 'difficulty'-doubleword you're reading */
                {
                    Utils.Debug("Notecount data seems bad, retrying fetching in case list wasn't fully populated.");
                    Thread.Sleep(5000);
                }
                else
                {
                    songlistFetched = true;
                }
            }
            File.Create(sessionFile.FullName).Close();
            WriteLine(sessionFile, Config.GetTsvHeader());

            /* Primarily for debugging and checking for encoding issues */
            if (Config.Output_songlist)
            {
                List<string> p = new List<string>() { "id\ttitle\ttitle2\tartist\tgenre" };
                foreach (var v in Utils.songDb)
                {
                    p.Add($"{v.Key}\t{v.Value.title}\t{v.Value.title_english}\t{v.Value.artist}\t{v.Value.genre}");
                }
                File.WriteAllLines("songs.tsv", p.ToArray());
            }

            Utils.GetUnlockStates();
            /* Check for new songs */
            if (Config.Save_remote && !init)
            {
                foreach (var song in Utils.songDb)
                {
                    if (!unlock_db.ContainsKey(song.Key) || init)
                    {
                        Network.UploadSongInfo(song.Key);
                    }
                    if (unlock_db[song.Key].Item1 != (int)Utils.unlockDb[song.Key].type)
                    {
                        Network.UpdateChartUnlockType(song.Value);
                    }
                    if (unlock_db[song.Key].Item2 != Utils.unlockDb[song.Key].unlocks)
                    {
                        Network.ReportUnlock(song.Key, Utils.unlockDb[song.Key]);
                        Thread.Sleep(40);
                    }
                    Thread.Sleep(10);
                }
            }


            GameState state = GameState.songSelect;

            Console.WriteLine("Initialized and ready");

            string chart = "";
            Utils.Debug("Updating marquee.txt");
            File.WriteAllText("marquee.txt", Config.MarqueeIdleText);
            /* Main loop */
            while (!process.HasExited)
            {
                var newstate = Utils.FetchGameState(state);
                Utils.Debug(newstate.ToString());
                if (newstate != state)
                {
                    Console.Clear();
                    Console.WriteLine($"STATUS:{(newstate != GameState.playing ? " NOT" : "")} PLAYING");
                    if (newstate == GameState.resultScreen)
                    {
                        Thread.Sleep(1000); /* Sleep to avoid race condition */
                        var latestData = new PlayData();
                        latestData.Fetch();
                        if (latestData.JudgedNotes != 0)
                        {
                            if (Config.Save_remote)
                            {
                                Network.SendPlayData(latestData);
                            }
                            if (Config.Save_local)
                            {
                                WriteLine(sessionFile, latestData.GetTsvEntry());
                            }
                            Print_PlayData(latestData);
                        }
                        if (Config.Stream_Playstate)
                        {
                            Utils.Debug("Writing menu state to playstate.txt");
                            File.WriteAllText("playstate.txt", "menu");
                        }
                        if (Config.Stream_Marquee)
                        {
                            Utils.Debug("Updating marquee.txt");
                            var clearstatus = latestData.ClearState == "F" ? "FAIL!" : "CLEAR!";
                            File.WriteAllText("marquee.txt", $"{chart} {clearstatus}");
                        }
                    }
                    else if (newstate == GameState.songSelect && Config.Stream_Marquee)
                    {
                        Utils.Debug("Updating marquee.txt");
                        File.WriteAllText("marquee.txt", Config.MarqueeIdleText);
                    }
                    else
                    {
                        if (Config.Stream_Playstate)
                        {
                            Utils.Debug("Writing play state to playstate.txt");
                            File.WriteAllText("playstate.txt", "play");
                        }
                        if (Config.Stream_Marquee)
                        {
                            chart = Utils.FetchCurrentChart();
                            Utils.Debug($"Writing {chart} to marquee.txt");
                            File.WriteAllText("marquee.txt", chart.ToUpper());
                        }
                    }
                }
                state = newstate;

                if (state == GameState.songSelect)
                {
                    var newUnlocks = Utils.UpdateUnlockStates();
                    Utils.SaveUnlockStates("unlocks.tsv");
                    if (newUnlocks.Count > 0)
                    {
                        Network.ReportUnlocks(newUnlocks);
                    }
                }

                Thread.Sleep(2000);
            }
            Utils.SaveUnlockStates("unlocks.tsv");
            if (Config.Stream_Playstate)
            {
                Utils.Debug("Writing menu state to playstate.txt");
                File.WriteAllText("playstate.txt", "off");
            }
            if (Config.Stream_Marquee)
            {
                Utils.Debug($"Writing NO SIGNAL to marquee.txt");
                File.WriteAllText("marquee.txt", "NO SIGNAL");
            }
        }

        static void Print_PlayData(PlayData latestData)
        {
            Console.WriteLine("\nLATEST CLEAR:");

            var header = Config.GetTsvHeader();
            var entry = latestData.GetTsvEntry();

            var h = header.Split('\t');
            var e = entry.Split('\t');
            for (int i = 0; i < h.Length; i++)
            {
                Console.WriteLine("{0,15}: {1,-50}", h[i], e[i]);
            }
        }
        static void WriteLine(FileInfo file, string str)
        {
            File.AppendAllLines(file.FullName, new string[] { str });
        }
        static void DumpStackMemory()
        {

        }

    }

    #region Custom objects
    enum GameState { playing = 0, resultScreen, songSelect };
    public enum PlayType { P1 = 0, P2, DP }
    #endregion
}
