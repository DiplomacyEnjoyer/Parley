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
          parley check-map <file.pmap>          load a map and print a summary
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
    case "check-map":
        {
            if (args.Length < 2) return Usage();
            var map = new Parley.Engine.MapData();
            if (!map.Load(File.ReadAllText(args[1]))) { Console.WriteLine("error: " + map.error); return 1; }
            Console.WriteLine($"{map.mapName} [{map.mapId}#{map.hash}]: {map.provCount} provinces, {map.locCount - map.provCount} extra coasts, {map.scCount} centres (victory {map.victoryCenters}), {map.powerCount} powers, {map.startUnitCount} starting units");
            return 0;
        }
    default:
        return Usage();
}
