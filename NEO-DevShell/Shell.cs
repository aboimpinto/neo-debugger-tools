﻿using LunarParser;
using LunarParser.JSON;
using Neo.Emulator;
using Neo.Emulator.API;
using Neo.Emulator.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;

namespace NEO_DevShell
{
    public abstract class Command
    {
        public Shell Shell { get; set; }

        public abstract string Name { get; }
        public abstract string Help { get; }

        public abstract void Execute(string[] args);
    }

    public class Shell
    {
        public List<Command> commands = new List<Command>();
        public NeoEmulator debugger;

        public string avmPath;
        public string storagePath;
        public string blockchainPath;


        public Shell()
        {
            this.debugger = null;

            AddCommand(new HelpCommand());
            AddCommand(new ExitCommand());
            AddCommand(new LoadCommand());
            AddCommand(new CallCommand());
            AddCommand(new StorageCommand());
        }

        private void AddCommand(Command cmd)
        {
            commands.Add(cmd);
            cmd.Shell = this;
        }

        internal static void Write(string s)
        {
            Console.WriteLine("\t" + s);
        }

        internal static string[] ParseArgs(string input)
        {
            var args = new List<string>();

            int pos = 0;
            var sb = new StringBuilder();

            char prev = '\0';

            while (pos < input.Length)
            {
                var c = input[pos];
                pos++;

                if (c == '"')
                {
                    sb.Append(c);
                    while (pos < input.Length)
                    {
                        c = input[pos];
                        pos++;

                        sb.Append(c);
                        if (c == '"') break;
                    }

                    args.Add(sb.ToString());
                    sb.Length = 0;
                }
                else
                if (c == '[')
                {
                    int temp = pos;

                    pos = input.Length - 1;
                    while (pos > temp)
                    {
                        c = input[pos];
                        if (c == ']') break;
                        pos--;
                    }


                    sb.Append('[');
                    while (temp<pos)
                    {
                        c = input[temp];
                        temp++;
                        sb.Append(c);
                    }
                    sb.Append(']');

                    pos = temp + 1;

                    args.Add(sb.ToString());
                    sb.Length = 0;
                }
                else
                {
                    if (c == ' ' || c==',')
                    {
                        if (sb.Length > 0)
                        {
                            args.Add(sb.ToString());
                            sb.Length = 0;
                        }
                    }
                    else
                        sb.Append(c);
                }

                prev = c;
            }

            if (sb.Length>0)
            {
                args.Add(sb.ToString());
            }

            return args.Where(x => !string.IsNullOrEmpty(x)).ToArray();
        }

        public bool Execute(string input)
        {
            var temp = ParseArgs(input);
            var cmd = temp[0].ToLower();

            foreach (var entry in commands)
            {
                if (entry.Name == cmd)
                {
                    entry.Execute(temp);
                    return true;
                }
            }

            return false;
        }
    }

    internal class StorageCommand : Command
    {
        public override string Name => "storage";
        public override string Help => "View content of storage";

        public override void Execute(string[] args)
        {
            foreach (var entry in Storage.entries)
            {
                Shell.Write(FormattingUtils.OutputData(entry.Key, false) + " => "+ FormattingUtils.OutputData(entry.Value, false, true));
            }
        }
    }

    internal class HelpCommand : Command
    {
        public override string Name => "help";
        public override string Help => "Prints this list of commands";

        public override void Execute(string[] args)
        {
            foreach (var cmd in Shell.commands)
            {
                Shell.Write(cmd.Name + ": " + cmd.Help);
            }
        }
    }

    internal class ExitCommand : Command
    {
        public override string Name => "exit";
        public override string Help => "Exits the shell";

        public override void Execute(string[] args)
        {
            Environment.Exit(0);
        }
    }

    internal class LoadCommand : Command
    {
        public override string Name => "load";
        public override string Help => "Loads a NEO smart contract from a file";

        public override void Execute(string[] args)
        {
            if (args.Length < 2) return;

            var filePath = args[1];

            if (File.Exists(filePath))
            {
                Shell.avmPath = filePath;

                var bytes = File.ReadAllBytes(filePath);
                Shell.debugger = new NeoEmulator(bytes);

                var avmName = Path.GetFileName(filePath);
                Shell.Write($"Loaded {avmName} ({bytes.Length} bytes)");

                Shell.storagePath = filePath.Replace(".avm", ".store");
                if (File.Exists(Shell.storagePath))
                {
                    Storage.Load(Shell.storagePath);
                    Shell.Write($"Loaded storage ({Storage.sizeInBytes} bytes, {Storage.entries.Count} entries)");
                }

                Shell.blockchainPath = filePath.Replace(".avm", ".chain");
                if (File.Exists(Shell.blockchainPath))
                {
                    Blockchain.Load(Shell.blockchainPath);
                    Shell.Write($"Loaded blockchain ({bytes.Length} bytes, {Storage.entries.Count} entries)");
                }

                Runtime.OnLogMessage = (x=> Shell.Write(x));
            }
            else
            {
                Shell.Write("File not found.");
            }

        }

    }

    internal class CallCommand : Command
    {
        public override string Name => "call";
        public override string Help => "Calls a smart contract method";

        public override void Execute(string[] args)
        {
            if (Shell.debugger == null)
            {
                Shell.Write("Smart contract not loaded yet.");
                return;
            }


            DataNode inputs;

            try
            {
                inputs = JSONReader.ReadFromString(args[1]);
            }
            catch
            {
                Shell.Write("Invalid arguments format. Must be valid JSON.");
                return;
            }

            if (args.Length>=3)
            {
                bool valid = false;

                if (args[2].ToLower() == "with")
                {
                    if (args.Length>=5)
                    {
                        BigInteger assetAmount = BigInteger.Parse(args[3]);
                        var assetName = args[4];

                        foreach (var entry in Asset.Entries)
                        {
                            if (entry.name == assetName)
                            {
                                Shell.Write($"Attaching {assetAmount} {assetName} to transaction");
                                Shell.debugger.AddTransaction(entry.id, assetAmount);
                                break;
                            }
                        }

                        valid = true;
                    }
                }
                
                if (!valid)
                {
                    Shell.Write("Invalid sintax.");
                    return;
                }

                Shell.Write("Executing transaction...");

                Shell.debugger.LoadInputs(inputs);

                Shell.debugger.Reset();

                Shell.debugger.Run();

                var val = Shell.debugger.GetResult();

                Storage.Save(Shell.storagePath);

                Blockchain.Save(Shell.blockchainPath);

                Shell.Write("Result: " + FormattingUtils.StackItemAsString(val));
                Shell.Write("GAS used: " + Shell.debugger.GetUsedGas());
            }
        }
    }

}