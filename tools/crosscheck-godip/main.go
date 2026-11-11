// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

// Command crosscheck-godip replays whole games exported by `parley crosscheck-games`
// in godip (the engine behind the Diplicity app) and compares the position after every
// phase: movement, retreats and builds.
//
// godip is GPL-3.0. This test program is built at setup time on your own machine and
// never distributed; nothing of godip becomes part of Parley.
//
//	cd tools/crosscheck-godip && go run . ../../games.jsonl
package main

import (
	"bufio"
	"encoding/json"
	"fmt"
	"os"
	"sort"
	"strings"

	"github.com/zond/godip"
	"github.com/zond/godip/state"
	"github.com/zond/godip/variants/classical"
)

type phase struct {
	Label   string              `json:"label"`
	Orders  map[string][]string `json:"orders"`
	Units   map[string][]string `json:"units"`
	Centres map[string][]string `json:"centres"`
	Next    string              `json:"next"`
	Forced  []string            `json:"forced"`
}

type game struct {
	Game   int     `json:"game"`
	Phases []phase `json:"phases"`
}

func label(s *state.State) string {
	p := s.Phase()
	season := string(p.Season())[0]
	if p.Type() == godip.Adjustment {
		season = 'W' // godip files builds under Fall; Parley calls the phase Winter
	}
	return fmt.Sprintf("%c%d%c", season, p.Year(), string(p.Type())[0])
}

// Parley's map (from DPjudge) and godip name four sea areas differently.
var toGodip = map[string]string{"mao": "mid", "nao": "nat", "nwg": "nrg", "lyo": "gol"}
var fromGodip = map[string]string{"mid": "mao", "nat": "nao", "nrg": "nwg", "gol": "lyo"}

func name(p string) string {
	p = strings.ToLower(p)
	base, coast, _ := strings.Cut(p, "/")
	if g, ok := toGodip[base]; ok {
		base = g
	}
	if coast != "" {
		return base + "/" + coast
	}
	return base
}

func back(p godip.Province) string {
	s := string(p)
	base, coast, _ := strings.Cut(s, "/")
	if g, ok := fromGodip[base]; ok {
		base = g
	}
	if coast != "" {
		base += "/" + coast
	}
	return strings.ToUpper(base)
}

func prov(s string) godip.Province { return godip.Province(name(s)) }

// "A PAR - BUR" etc. in the export's notation -> godip's [province, type, args...]
func bits(order string) []string {
	w := strings.Fields(order)
	if len(w) < 3 {
		return nil
	}
	src := name(w[1])
	switch w[2] {
	case "H":
		return []string{src, "Hold"}
	case "-":
		if len(w) == 5 && w[4] == "VIA" {
			return []string{src, "MoveViaConvoy", name(w[3])}
		}
		return []string{src, "Move", name(w[3])}
	case "S":
		if len(w) == 5 {
			t := name(w[4])
			return []string{src, "Support", t, t}
		}
		return []string{src, "Support", name(w[4]), name(w[6])}
	case "C":
		return []string{src, "Convoy", name(w[4]), name(w[6])}
	case "R":
		return []string{src, "Move", name(w[3])}
	case "D":
		return []string{src, "Disband"}
	case "B":
		t := "Army"
		if w[0] == "F" {
			t = "Fleet"
		}
		return []string{src, "Build", t}
	}
	return nil
}

func unitText(u godip.Unit, p godip.Province) string {
	return string(u.Type)[0:1] + " " + back(p)
}

func main() {
	f, err := os.Open(os.Args[1])
	if err != nil {
		panic(err)
	}
	in := bufio.NewScanner(f)
	in.Buffer(make([]byte, 64<<20), 64<<20)
	games, phases := 0, 0
	same := map[byte]int{}
	all := map[byte]int{}
	known, other := 0, 0
	shown := 0
	for in.Scan() {
		var g game
		if err := json.Unmarshal(in.Bytes(), &g); err != nil {
			panic(err)
		}
		games++
		s, err := classical.Start()
		if err != nil {
			panic(err)
		}
		for _, ph := range g.Phases {
			kind := ph.Label[len(ph.Label)-1]
			// godip always passes through retreat and adjustment phases, even empty ones
			for i := 0; i < 2 && label(s) != ph.Label && len(s.Units()) > 0; i++ {
				t := label(s)
				if t[len(t)-1] == 'M' {
					break
				}
				s.SetOrders(map[godip.Province]godip.Adjudicator{})
				if err := s.Next(); err != nil {
					panic(err)
				}
			}
			if label(s) != ph.Label {
				other++
				if shown < 10 {
					fmt.Printf("--- game %d: parley is at %s, godip at %s\n", g.Game, ph.Label, label(s))
					shown++
				}
				break
			}
			orders := map[godip.Province]godip.Adjudicator{}
			for _, os_ := range ph.Orders {
				for _, o := range os_ {
					b := bits(o)
					if b == nil {
						continue
					}
					a, err := classical.Parser.Parse(b)
					if err != nil || a == nil {
						continue
					}
					orders[godip.Province(b[0])] = a
				}
			}
			s.SetOrders(orders)
			if err := s.Next(); err != nil {
				panic(err)
			}
			// pass through phases godip holds and Parley skips (an empty retreat phase, or an
			// adjustment phase where nobody builds or disbands); godip updates centres on the way
			target := ph.Next
			if strings.HasSuffix(target, "X") {
				target = "W" + target[1:len(target)-1] + "A" // game over after the autumn: godip still goes on to winter
			}
			for i := 0; i < 2 && label(s) != target; i++ {
				t := label(s)
				if t[len(t)-1] == 'M' || (t[len(t)-1] == 'R' && len(s.Dislodgeds()) > 0) {
					break
				}
				s.SetOrders(map[godip.Province]godip.Adjudicator{})
				if err := s.Next(); err != nil {
					panic(err)
				}
			}
			phases++
			all[kind]++
			theirs := map[string][]string{}
			for p := range ph.Units {
				theirs[p] = []string{}
			}
			for p, u := range s.Units() {
				n := strings.ToUpper(string(u.Nation))
				theirs[n] = append(theirs[n], unitText(u, p))
			}
			for p, u := range s.Dislodgeds() {
				n := strings.ToUpper(string(u.Nation))
				theirs[n] = append(theirs[n], "*"+unitText(u, p))
			}
			centres := map[string][]string{}
			for p := range ph.Centres {
				centres[p] = []string{}
			}
			for p, n := range s.SupplyCenters() {
				centres[strings.ToUpper(string(n))] = append(centres[strings.ToUpper(string(n))], back(p))
			}
			ok := true
			for p := range ph.Units {
				a := append([]string{}, ph.Units[p]...)
				b := theirs[p]
				sort.Strings(a)
				sort.Strings(b)
				// godip may remove a dislodged unit with nowhere to go at once
				if strings.Join(a, ",") != strings.Join(b, ",") {
					onlyStars := true
					bs := map[string]bool{}
					for _, x := range b {
						bs[x] = true
					}
					for _, x := range a {
						if !bs[x] && !strings.HasPrefix(x, "*") {
							onlyStars = false
						}
					}
					if !onlyStars || len(b) > len(a) {
						ok = false
					}
				}
				c1 := append([]string{}, ph.Centres[p]...)
				c2 := centres[p]
				sort.Strings(c1)
				sort.Strings(c2)
				if strings.Join(c1, ",") != strings.Join(c2, ",") {
					ok = false
				}
			}
			if ok {
				same[kind]++
				// the game's end: parley says W1913X; godip simply continues
				if strings.HasSuffix(ph.Next, "X") {
					break
				}
				continue
			}
			isKnown := kind == 'A' && len(ph.Forced) > 0
			if kind == 'M' {
				for _, os_ := range ph.Orders {
					for _, o := range os_ {
						if w := strings.Fields(o); len(w) == 7 && w[2] == "C" {
							if s.Graph().Edges(prov(w[4]), false)[prov(w[6])] != nil {
								isKnown = true
							}
						}
					}
				}
			}
			if isKnown {
				known++
			} else {
				other++
				if shown < 10 {
					shown++
					fmt.Printf("--- game %d phase %s\n", g.Game, ph.Label)
					for p, os_ := range ph.Orders {
						if len(os_) > 0 {
							fmt.Println("  ", p, strings.Join(os_, "; "))
						}
					}
					for p := range ph.Units {
						a := append([]string{}, ph.Units[p]...)
						sort.Strings(a)
						b := theirs[p]
						sort.Strings(b)
						if strings.Join(a, ",") != strings.Join(b, ",") {
							fmt.Println("   parley:", p, a)
							fmt.Println("   godip :", p, b)
						}
						c1 := append([]string{}, ph.Centres[p]...)
						sort.Strings(c1)
						c2 := centres[p]
						sort.Strings(c2)
						if strings.Join(c1, ",") != strings.Join(c2, ",") {
							fmt.Println("   parley centres:", p, c1)
							fmt.Println("   godip centres :", p, c2)
						}
					}
				}
			}
			break
		}
	}
	fmt.Printf("%d games replayed in godip, %d phases compared\n", games, phases)
	for _, k := range []struct {
		c    byte
		name string
	}{{'M', "movement"}, {'R', "retreat"}, {'A', "build/disband"}} {
		fmt.Printf("  %-14s: %d of %d identical\n", k.name, same[k.c], all[k.c])
	}
	fmt.Printf("%d games stopped at a known rule-edition difference, %d at another difference\n", known, other)
	if other > 0 {
		os.Exit(1)
	}
}
