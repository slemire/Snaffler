﻿using CommandLineParser.Arguments;
using Nett;
using NLog;
using SnaffCore.Concurrency;
using SnaffCore.Config;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;

namespace Snaffler
{
    public static class Config
    {
        public static Options Parse(string[] args)
        {
            BlockingMq Mq = BlockingMq.GetMq();
            Options options;

            // parse the args
            try
            {
                options = ParseImpl(args);
                if (options == null)
                {
                    throw new ArgumentException("Unable to correctly parse arguments.");
                }
            }
            catch
            {
                Mq.Error("Something went wrong parsing args.");
                throw;
            }

            Mq.Info("Parsed args successfully.");
            return options;
        }

        private static Options ParseImpl(string[] args)
        {
            BlockingMq Mq = BlockingMq.GetMq();
            Mq.Info("Parsing args...");
            Options parsedConfig = new Options();

            // define args
            ValueArgument<string> configFileArg = new ValueArgument<string>('z', "config", "Path to a .toml config file. Run with \'generate\' to puke a sample config file into the working directory.");
            ValueArgument<string> outFileArg = new ValueArgument<string>('o', "outfile",
                "Path for output file. You probably want this if you're not using -s.");
            ValueArgument<string> verboseArg = new ValueArgument<string>('v', "verbosity",
                "Controls verbosity level, options are Trace (most verbose), Debug (less verbose), Info (less verbose still, default), and Data (results only). e.g '-v debug' ");
            SwitchArgument helpArg = new SwitchArgument('h', "help", "Displays this help.", false);
            SwitchArgument stdOutArg = new SwitchArgument('s', "stdout",
                "Enables outputting results to stdout as soon as they're found. You probably want this if you're not using -o.",
                false);
            ValueArgument<int> interestLevel = new ValueArgument<int>('b', "interest", "Interest level to report (0-3)");
            ValueArgument<string> snaffleArg = new ValueArgument<string>('m', "snaffle",
                "Enables and assigns an output dir for Snaffler to automatically snaffle a copy of any found files.");
            ValueArgument<long> snaffleSizeArg = new ValueArgument<long>('l', "snafflesize", "Maximum size of file to snaffle, in bytes. Defaults to 10MB.");
            //var fileHuntArg = new SwitchArgument('f', "filehuntoff",
            //    "Disables file discovery, will only perform computer and share discovery.", false);
            ValueArgument<string> dirTargetArg = new ValueArgument<string>('i', "dirtarget",
                "Disables computer and share discovery, requires a path to a directory in which to perform file discovery.");
            ValueArgument<string> domainArg = new ValueArgument<string>('d', "domain",
                "Domain to search for computers to search for shares on to search for files in. Easy.");
            ValueArgument<string> domainControllerArg = new ValueArgument<string>('c', "domaincontroller",
                "Domain controller to query for a list of domain computers.");
            ValueArgument<long> maxGrepSizeArg = new ValueArgument<long>('r', "maxgrepsize",
                "The maximum size file (in bytes) to search inside for interesting strings. Defaults to 500k.");
            ValueArgument<int> grepContextArg = new ValueArgument<int>('j', "grepcontext",
                "How many bytes of context either side of found strings in files to show, e.g. -j 200");
            SwitchArgument domainUserArg = new SwitchArgument('u', "domainusers", "Makes Snaffler grab a list of interesting-looking accounts from the domain and uses them in searches.", false);
            ValueArgument<int> maxThreadsArg = new ValueArgument<int>('x', "maxthreads", "How many threads to be snaffling with. Any less than 4 and you're gonna have a bad time.");
            SwitchArgument tsvArg = new SwitchArgument('y', "tsv", "Makes Snaffler output as tsv.", false);
            SwitchArgument dfsArg = new SwitchArgument('f', "dfs", "Limits Snaffler to finding file shares via DFS, for \"OPSEC\" reasons.", false);
            SwitchArgument findSharesOnlyArg = new SwitchArgument('a', "sharesonly",
                "Stops after finding shares, doesn't walk their filesystems.", false);
            ValueArgument<string> compTargetArg = new ValueArgument<string>('n', "comptarget", "Computer (or comma separated list) to target.");
            // list of letters i haven't used yet: egknpqw

            CommandLineParser.CommandLineParser parser = new CommandLineParser.CommandLineParser();
            parser.Arguments.Add(configFileArg);
            parser.Arguments.Add(outFileArg);
            parser.Arguments.Add(helpArg);
            parser.Arguments.Add(stdOutArg);
            parser.Arguments.Add(snaffleArg);
            parser.Arguments.Add(snaffleSizeArg);
            parser.Arguments.Add(dirTargetArg);
            parser.Arguments.Add(interestLevel);
            parser.Arguments.Add(domainArg);
            parser.Arguments.Add(verboseArg);
            parser.Arguments.Add(domainControllerArg);
            parser.Arguments.Add(maxGrepSizeArg);
            parser.Arguments.Add(grepContextArg);
            parser.Arguments.Add(domainUserArg);
            parser.Arguments.Add(tsvArg);
            parser.Arguments.Add(dfsArg);
            parser.Arguments.Add(findSharesOnlyArg);
            parser.Arguments.Add(maxThreadsArg);
            parser.Arguments.Add(compTargetArg);

            // extra check to handle builtin behaviour from cmd line arg parser
            if ((args.Contains("--help") || args.Contains("/?") || args.Contains("help") || args.Contains("-h") || args.Length == 0))
            {
                parser.ShowUsage();
                Environment.Exit(0);
            }

            TomlSettings settings = TomlSettings.Create(cfg => cfg
.ConfigureType<LogLevel>(tc =>
    tc.WithConversionFor<TomlString>(conv => conv
        .FromToml(s => (LogLevel)Enum.Parse(typeof(LogLevel), s.Value, ignoreCase: true))
        .ToToml(e => e.ToString()))));

            try
            {
                parser.ParseCommandLine(args);

                if (configFileArg.Parsed)
                {
                    if (!configFileArg.Value.Equals("generate"))
                    {
                        string configFile = configFileArg.Value;
                        parsedConfig = Toml.ReadFile<Options>(configFile, settings);
                        parsedConfig.PrepareClassifiers();
                        Mq.Info("Read config file from " + configFile);
                        return parsedConfig;
                    }
                }

                if (parsedConfig.ClassifierRules.Count <= 0)
                {
                    parsedConfig.BuildDefaultClassifiers();
                }
                // get the args into our config

                // output args
                if (outFileArg.Parsed && (!String.IsNullOrEmpty(outFileArg.Value)))
                {
                    parsedConfig.LogToFile = true;
                    parsedConfig.LogFilePath = outFileArg.Value;
                    Mq.Degub("Logging to file at " + parsedConfig.LogFilePath);
                }

                if (dfsArg.Parsed)
                {
                    parsedConfig.DfsOnly = dfsArg.Value;
                }

                if (compTargetArg.Parsed)
                {
                    string[] compTargets = null;
                    List<string> compTargetsList = new List<string>();

                    if (compTargetArg.Value.Contains(","))
                    {                        
                        string[] compTargetsUnprocessed = compTargetArg.Value.Split(',');
                        foreach (string compTarget in compTargetsUnprocessed)
                        {
                            // match cidr notation
                            string cidr = @"/\d{1,3}$";
                            Regex r = new Regex(cidr, RegexOptions.IgnoreCase);
                            Match m = r.Match(compTarget);
                            if (m.Success)
                            {
                                IPNetwork ipn = IPNetwork.Parse(compTarget);
                                IPAddressCollection ips = IPNetwork.ListIPAddress(ipn);                                
                                foreach (IPAddress ip in ips)
                                {
                                    compTargetsList.Add(ip.ToString());                                    
                                }
                                compTargets = compTargetsList.ToArray();
                            }
                            else
                            {
                                compTargetsList.Add(compTarget);
                            }
                        }
                        compTargets = compTargetsList.ToArray();                        
                    }
                    else
                    {
                        // match cidr notation
                        string cidr = @"/\d{1,3}$";
                        Regex r = new Regex(cidr, RegexOptions.IgnoreCase);
                        Match m = r.Match(compTargetArg.Value);
                        if (m.Success)
                        {
                            IPNetwork ipn = IPNetwork.Parse(compTargetArg.Value);
                            IPAddressCollection ips = IPNetwork.ListIPAddress(ipn);
                            compTargetsList = new List<string>();
                            foreach (IPAddress ip in ips)
                            {
                                compTargetsList.Add(ip.ToString());                                
                            }
                            compTargets = compTargetsList.ToArray();
                        }
                        else
                        {
                            compTargets = new string[] { compTargetArg.Value };
                        }                        
                    }                    
                    parsedConfig.ComputerTargets = compTargets;
                }

                if (findSharesOnlyArg.Parsed)
                {
                    parsedConfig.ScanFoundShares = false;
                }
                if (maxThreadsArg.Parsed)
                {
                    parsedConfig.MaxThreads = maxThreadsArg.Value;
                }

                parsedConfig.ShareThreads = parsedConfig.MaxThreads / 3;
                parsedConfig.FileThreads = parsedConfig.MaxThreads / 3;
                parsedConfig.TreeThreads = parsedConfig.MaxThreads / 3;

                if (tsvArg.Parsed)
                {
                    parsedConfig.LogTSV = true;
                    if (parsedConfig.Separator == ' ')
                    {
                        parsedConfig.Separator = '\t';
                    }
                }

                // Set loglevel.
                if (verboseArg.Parsed)
                {
                    parsedConfig.LogLevelString = verboseArg.Value;
                    Mq.Degub("Requested verbosity level: " + parsedConfig.LogLevelString);
                }

                // if enabled, display findings to the console
                parsedConfig.LogToConsole = stdOutArg.Parsed;
                Mq.Degub("Enabled logging to stdout.");

                // args that tell us about targeting
                if ((domainArg.Parsed) && (!String.IsNullOrEmpty(domainArg.Value)))
                {
                    parsedConfig.TargetDomain = domainArg.Value;
                    Mq.Degub("Target domain is " + domainArg.Value);
                }

                if ((domainControllerArg.Parsed) && (!String.IsNullOrEmpty(domainControllerArg.Value)))
                {
                    parsedConfig.TargetDc = domainControllerArg.Value;
                    Mq.Degub("Target DC is " + domainControllerArg.Value);
                }

                if (domainUserArg.Parsed)
                {
                    parsedConfig.DomainUserRules = true;
                    Mq.Degub("Enabled use of domain user accounts in rules.");
                }

                if (dirTargetArg.Parsed)
                {
                    parsedConfig.ShareFinderEnabled = false;
                    parsedConfig.PathTargets.Add(dirTargetArg.Value);
                    Mq.Degub("Disabled finding shares.");
                    Mq.Degub("Target path is " + dirTargetArg.Value);
                }

                if (maxGrepSizeArg.Parsed)
                {
                    parsedConfig.MaxSizeToGrep = maxGrepSizeArg.Value;
                    Mq.Degub("We won't bother looking inside files if they're bigger than " + parsedConfig.MaxSizeToGrep +
                             " bytes");
                }

                if (snaffleSizeArg.Parsed)
                {
                    parsedConfig.MaxSizeToSnaffle = snaffleSizeArg.Value;
                }

                if (interestLevel.Parsed)
                {
                    parsedConfig.InterestLevel = interestLevel.Value;
                    Mq.Degub("Requested interest level: " + parsedConfig.InterestLevel);
                }

                // how many bytes 
                if (grepContextArg.Parsed)
                {
                    parsedConfig.MatchContextBytes = grepContextArg.Value;
                    Mq.Degub(
                        "We'll show you " + grepContextArg.Value +
                        " bytes of context around matches inside files.");
                }

                // if enabled, grab a copy of files that we like.
                if (snaffleArg.Parsed)
                {
                    if (snaffleArg.Value.Length <= 0)
                    {
                        Mq.Error("-m or -mirror arg requires a path value.");
                        throw new ArgumentException("Invalid argument combination.");
                    }

                    parsedConfig.Snaffle = true;
                    parsedConfig.SnafflePath = snaffleArg.Value.TrimEnd('\\');
                    Mq.Degub("Mirroring matched files to path " + parsedConfig.SnafflePath);
                }

                if (configFileArg.Parsed)
                {
                    if (configFileArg.Value.Equals("generate"))
                    {
                        Toml.WriteFile(parsedConfig, ".\\default.toml", settings);
                        Mq.Info("Wrote default config values to .\\default.toml");
                        Mq.Terminate();
                        parsedConfig.LogToConsole = true;
                        Mq.Degub("Enabled logging to stdout.");
                    }
                }

                if (!parsedConfig.LogToConsole && !parsedConfig.LogToFile)
                {
                    Mq.Error(
                        "\nYou didn't enable output to file or to the console so you won't see any results or debugs or anything. Your l0ss.");
                    throw new ArgumentException("Pointless argument combination.");
                }

                parsedConfig.PrepareClassifiers();
            }
            catch (Exception e)
            {
                Mq.Error(e.ToString());
                throw;
            }

            return parsedConfig;
        }



    }
}
