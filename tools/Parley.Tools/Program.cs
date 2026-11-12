// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
using Parley.Tools;

static int Usage()
{
    Console.WriteLine("""
        parley - tools for the Parley engine

          parley datc [case-id]                 run the DATC rule test cases
          parley import-dpjudge <in.map> <out.pmap> [id] [name...]
                                                convert a DPjudge-format map
          parley import-svg <map.pmap> <board.svg> <out.art.json> [dpjudge.map]
                                                match a jDip-style board picture to a map (for the web client)
          parley import-jdip <pack.zip> [variant] [out.pmap] [id]
                                                convert a jDip variant (no variant: list them); writes out.art.json too
          parley import-njudge-mach <map.machiavelli> <seed.9> <out.pmap> [id]
                                                convert the nJudge Renaissance-Italy board (Purse and Dagger ruleset)
          parley purse-sim <file.pmap> [games] [seed] [years]
                                                random play of the Purse and Dagger ruleset with consistency checks
          parley check-map <file.pmap>          load a map and print a summary
          parley sim <file.pmap> [games] [seed] [maxYears] [basic|random]
                                                play bot games; checks + per-power statistics
          parley crosscheck-export <file.pmap> [count] [seed]
                                                write random situations + verdicts as JSON lines
          parley crosscheck-games <file.pmap> [games] [seed] [years]
                                                write whole bot games (all phase types) as JSON lines
          parley gen-ring <players> [isthmus] [out.pmap]
                                                generate an original Ring map
        """);
    return 2;
}

if (args.Length == 0) return Usage();
switch (args[0])
{
    case "datc":
        {
            var results = DatcRunner.RunAll(args.Length > 1 ? args[1] : null);
            int failed = 0;
            foreach (var r in results)
            {
                if (r.Passed) continue;
                failed++;
                Console.WriteLine($"FAIL {r.Id} {r.Title}");
                foreach (var f in r.Failures) Console.WriteLine("     " + f);
            }
            Console.WriteLine($"DATC: {results.Count - failed} of {results.Count} cases pass.");
            return failed == 0 ? 0 : 1;
        }
    case "import-dpjudge":
        {
            if (args.Length < 3) return Usage();
            var res = DpjudgeImporter.Import(args[1], args.Length > 3 ? args[3] : null, args.Length > 4 ? string.Join(' ', args.Skip(4)) : null);
            foreach (var w in res.Warnings) Console.WriteLine("warning: " + w);
            var map = new Parley.Engine.MapData();
            if (!map.Load(res.Pmap)) { Console.WriteLine("converted map does not load: " + map.error); return 1; }
            File.WriteAllText(args[2], res.Pmap);
            Console.WriteLine($"wrote {args[2]}: {map.provCount} provinces, {map.scCount} centres, {map.powerCount} powers");
            return 0;
        }
    case "import-svg":
        {
            if (args.Length < 4) return Usage();
            var map = new Parley.Engine.MapData();
            if (!map.Load(File.ReadAllText(args[1]))) { Console.WriteLine("map does not load: " + map.error); return 1; }
            var aliases = args.Length > 4 ? DpjudgeImporter.Import(args[4]).Aliases : new Dictionary<string, List<string>>();
            var warnings = new List<string>();
            var art = SvgArt.Convert(args[2], map, aliases, warnings);
            foreach (var w in warnings) Console.WriteLine("warning: " + w);
            SvgArt.Write(art, args[3]);
            Console.WriteLine($"wrote {args[3]}: {art.Provs.Count} province shapes, {art.Units.Count} unit positions, {art.Centres.Count} centre markers");
            return 0;
        }
    case "import-njudge-mach":
        {
            if (args.Length < 4) return Usage();
            var res = NjudgeMachImporter.Import(args[1], args[2], args.Length > 4 ? args[4] : "italy1454");
            foreach (var w in res.Warnings) Console.WriteLine("warning: " + w);
            var map = new Parley.Engine.MapData();
            if (!map.Load(res.Pmap)) { Console.WriteLine("converted map does not load: " + map.error); File.WriteAllText(args[3] + ".failed", res.Pmap); return 1; }
            File.WriteAllText(args[3], res.Pmap);
            Console.WriteLine($"wrote {args[3]}: {map.landCount} provinces, {map.provCount - map.landCount} walled cities, {map.powerCount} powers");
            return 0;
        }
    case "purse-sim":
        {
            if (args.Length < 2) return Usage();
            var rep = PurseSim.Run(File.ReadAllText(args[1]), args.Length > 2 ? int.Parse(args[2]) : 20, args.Length > 3 ? int.Parse(args[3]) : 1, args.Length > 4 ? int.Parse(args[4]) : 12);
            foreach (var pr in rep.Problems) Console.WriteLine("PROBLEM " + pr);
            Console.WriteLine($"{rep.Games} games, {rep.Phases} phases, {rep.Finished} finished, {rep.Problems.Count} problems");
            Console.WriteLine(string.Join(", ", rep.Seen.OrderByDescending(k => k.Value).Select(k => k.Key + " " + k.Value)));
            return rep.Problems.Count == 0 ? 0 : 1;
        }
    case "import-jdip":
        {
            if (args.Length < 2) return Usage();
            if (args.Length < 4) { foreach (var n in JdipImporter.List(args[1])) Console.WriteLine(n); return 0; }
            string id = args.Length > 4 ? args[4] : Path.GetFileNameWithoutExtension(args[3]);
            var res = JdipImporter.Import(args[1], args[2], id);
            foreach (var w in res.Warnings) Console.WriteLine("warning: " + w);
            var map = new Parley.Engine.MapData();
            if (!map.Load(res.Pmap)) { Console.WriteLine("converted map does not load: " + map.error); File.WriteAllText(args[3] + ".failed", res.Pmap); return 1; }
            File.WriteAllText(args[3], res.Pmap);
            Console.WriteLine($"wrote {args[3]}: {map.provCount} provinces, {map.scCount} centres, {map.powerCount} powers");
            if (res.Svg != "")
            {
                string tmp = Path.GetTempFileName();
                File.WriteAllText(tmp, res.Svg);
                var warnings = new List<string>();
                try
                {
                    var art = SvgArt.Convert(tmp, map, res.Aliases, warnings);
                    string artFile = Path.ChangeExtension(args[3], ".art.json");
                    SvgArt.Write(art, artFile);
                    foreach (var w in warnings) Console.WriteLine("art warning: " + w);
                    Console.WriteLine($"wrote {artFile}: {art.Provs.Count} province shapes");
                }
                catch (Exception ex) { Console.WriteLine("no picture: " + ex.Message); }
                finally { File.Delete(tmp); }
            }
            return 0;
        }
    case "crosscheck-games":
        {
            if (args.Length < 2) return Usage();
            CrossCheck.ExportGames(File.ReadAllText(args[1]), args.Length > 2 ? int.Parse(args[2]) : 50, args.Length > 3 ? int.Parse(args[3]) : 1,
                args.Length > 4 ? int.Parse(args[4]) : 12, Console.Out);
            return 0;
        }
    case "check-map":
        {
            if (args.Length < 2) return Usage();
            var map = new Parley.Engine.MapData();
            if (!map.Load(File.ReadAllText(args[1]))) { Console.WriteLine("error: " + map.error); return 1; }
            Console.WriteLine($"{map.mapName} [{map.mapId}#{map.hash}]: {map.provCount} provinces, {map.locCount - map.provCount} extra coasts, {map.scCount} centres (victory {map.victoryCenters}), {map.powerCount} powers, {map.startUnitCount} starting units");
            return 0;
        }
    case "sim":
        {
            if (args.Length < 2) return Usage();
            int games = args.Length > 2 ? int.Parse(args[2]) : 100;
            int seed = args.Length > 3 ? int.Parse(args[3]) : 1;
            int years = args.Length > 4 ? int.Parse(args[4]) : 40;
            string kind = args.Length > 5 ? args[5] : "basic";
            var rep = Simulator.Run(File.ReadAllText(args[1]), games, seed, years, kind, true, Console.WriteLine);
            Console.Write(Simulator.Format(rep));
            return rep.Problems.Count == 0 ? 0 : 1;
        }
    case "crosscheck-export":
        {
            if (args.Length < 2) return Usage();
            CrossCheck.Export(File.ReadAllText(args[1]), args.Length > 2 ? int.Parse(args[2]) : 1000, args.Length > 3 ? int.Parse(args[3]) : 1, Console.Out);
            return 0;
        }
    case "gen-ring":
        {
            if (args.Length < 2 || !int.TryParse(args[1], out int np)) return Usage();
            bool isthmus = args.Contains("isthmus");
            var text = RingMapGenerator.Generate(np, isthmus);
            var map = new Parley.Engine.MapData();
            if (!map.Load(text)) { Console.WriteLine("generated map does not load: " + map.error); return 1; }
            var outPath = args.Skip(2).FirstOrDefault(a => a.EndsWith(".pmap"));
            if (outPath == null) Console.Write(text);
            else { File.WriteAllText(outPath, text); Console.WriteLine($"wrote {outPath}: {map.provCount} provinces, {map.scCount} centres, {map.powerCount} powers [{map.mapId}#{map.hash}]"); }
            return 0;
        }
    default:
        return Usage();
}
