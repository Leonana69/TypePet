---
name: rankx
kind: script
hosts: nexon.com, maple.gg, dakgg.io, maple-kit.com
usage: /rankx [-na|-eu|-kr|-sea|-tw] <character>
help: (JS port of /rank) MapleStory character lookup — -na/-eu = GMS (with a global rank), -kr = KMS, -sea = MSEA, -tw = TMS. Network must be approved in the Commands tab.
holdSeconds: 20
---
// A user-definable port of the built-in /rank: it fetches and parses the ranking data here in JavaScript,
// then hands a structured object to the core via rank({...}) — the EXP bar + layout are rendered by the app
// (RankRenderer), so this file owns only the "where to fetch / how to parse" half.
//
// Network: the `hosts:` allowlist above is enforced by the host; httpGet can only reach those (over HTTPS),
// and only after you approve this command's network access in the Commands tab.

// ---- server table (mirrors RankServers.cs) -----------------------------------------------------------
var SERVERS = {
  na:  { label: "GMS NA", kind: "gms", base: "https://www.nexon.com/api/maplestory/no-auth/ranking/v2", code: "na",
         infoSite: "MapleRanks", info: "https://mapleranks.com/u/{n}" },
  eu:  { label: "GMS EU", kind: "gms", base: "https://www.nexon.com/api/maplestory/no-auth/ranking/v2", code: "eu",
         infoSite: "MapleRanks", info: "https://mapleranks.com/u/{n}" },
  kr:  { label: "KMS", kind: "maplegg", data: "https://maple.gg/u/{n}",
         stats: "https://maple.dakgg.io/api/v1/characters/{n}/profile", infoSite: "maple.gg", info: "https://maple.gg/u/{n}" },
  sea: { label: "MSEA", kind: "maplegg", data: "https://msea.maple.gg/u/{n}",
         stats: "https://msea.dakgg.io/api/v1/characters/{n}/profile", infoSite: "maple.gg", info: "https://msea.maple.gg/u/{n}" },
  tw:  { label: "TMS", kind: "maplekit", data: "https://maple-kit.com/api/character?character_name={n}",
         infoSite: "maple-kit.com", info: "https://maple-kit.com/character/{n}" }
};

// worldID -> name (GMS rankings bundle); Kronos(45)/Hyperion(70) are Heroic worlds.
var GMS_WORLDS = {
  0:"Scania",1:"Bera",2:"Broa",3:"Windia",4:"Khaini",5:"Bellocan",6:"Mardia",7:"Kradia",8:"Yellonde",
  9:"Demethos",10:"Galicia",11:"El Nido",12:"Zenith",13:"Arcania",14:"Chaos",15:"Nova",16:"Renegades",
  17:"Aurora",18:"Elysium",19:"Scania",30:"Luna",45:"Kronos",46:"Solis",48:"Challengers",49:"Challengers",
  52:"Challengers Heroic",54:"Challengers Heroic",70:"Hyperion"
};

// GMS "EXP to next level", index = level (1..299); [0] and [300] are placeholders. The GMS ranking row gives
// raw exp within the level but no %, so percent = exp / GMS_EXP[level] * 100.
var GMS_EXP = [
  0, 15, 34, 57, 92, 135, 372, 560,
  840, 1242, 1242, 1242, 1242, 1242, 1242, 1490,
  1788, 2145, 2574, 3088, 3705, 4446, 5335, 6402,
  7682, 9218, 11061, 13273, 15927, 19112, 19112, 19112,
  19112, 19112, 19112, 22934, 27520, 33024, 39628, 47553,
  51357, 55465, 59902, 64694, 69869, 75458, 81494, 88013,
  95054, 102658, 110870, 119739, 129318, 139663, 150836, 162902,
  175934, 190008, 205208, 221624, 221624, 221624, 221624, 221624,
  221624, 238245, 256113, 275321, 295970, 318167, 342029, 367681,
  395257, 424901, 456768, 488741, 522952, 559558, 598727, 640637,
  685481, 733464, 784806, 839742, 898523, 961419, 1028718, 1100728,
  1177778, 1260222, 1342136, 1429374, 1522283, 1621231, 1726611, 1838840,
  1958364, 2085657, 2221224, 2365603, 2365603, 2365603, 2365603, 2365603,
  2365603, 2519367, 2683125, 2857528, 3043267, 3241079, 3451749, 3676112,
  3915059, 4169537, 4440556, 4729192, 5036589, 5363967, 5712624, 6083944,
  6479400, 6900561, 7349097, 7826788, 8335529, 8877338, 9454364, 10068897,
  10723375, 11420394, 12162719, 12953295, 13795259, 14691950, 15646926, 16663976,
  17747134, 18900697, 20129242, 21437642, 22777494, 24201087, 25713654, 27320757,
  29028304, 30842573, 32770233, 34818372, 36994520, 39306677, 41763344, 44373553,
  47146900, 50093581, 53224429, 56550955, 60085389, 63840725, 67830770, 72070193,
  76574580, 81360491, 86445521, 91848366, 97588888, 103688193, 110168705, 117054249,
  124370139, 132143272, 138750435, 145687956, 152972353, 160620970, 168652018, 177084618,
  185938848, 195235790, 204997579, 215247457, 226009829, 237310320, 249175836, 261634627,
  274716358, 288452175, 302874783, 318018522, 333919448, 350615420, 368146191, 386553500,
  405881175, 426175233, 447483994, 469858193, 493351102, 518018657, 543919589, 571115568,
  2207026470, 2471869646, 2768494003, 3100713283, 3472798876, 3889534741, 4356278909, 4879032378,
  5464516263, 6120258214, 7344309856, 8152183940, 9048924173, 10044305832, 11149179473, 13379015367,
  14583126750, 15895608157, 17326212891, 18885572051, 22662686461, 24249074513, 25946509728, 27762765408,
  29706158986, 35647390783, 38142708137, 40812697706, 43669586545, 46726457603, 56071749123, 57753901596,
  59486518643, 61271114202, 63109247628, 75731097153, 78003030067, 80343120969, 82753414598, 85236017035,
  102283220442, 105351717055, 108512268566, 111767636622, 115120665720, 138144798864, 142289142829, 146557817113,
  150954551626, 155483188174, 186579825808, 192177220582, 197942537199, 203880813314, 209997237713, 216297154844,
  222786069489, 229469651573, 236353741120, 243444353353, 1731919984062, 1749239183902, 1766731575741, 1784398891498,
  1802242880412, 2342915744535, 2366344901980, 2390008350999, 2413908434508, 2438047518853, 5412465491853, 5466590146771,
  5521256048238, 5576468608720, 5632233294807, 11377111255510, 12514822381061, 13766304619167, 15142935081083, 16657228589191,
  33647601750165, 37012361925181, 40713598117699, 44784957929468, 49263453722414, 99512176519276, 109463394171203, 120409733588323,
  132450706947155, 145695777641870, 294305470836577, 323736017920234, 356109619712257, 391720581683483, 430892639851831, 870403132500699,
  957443445750769, 1053187790325845, 1158506569358425, 1737759854037637, 0
];

// ---- small helpers -----------------------------------------------------------------------------------
function parseInput(s) {
  s = (s || "").trim();
  var flag = "na", name = s;
  if (s.charAt(0) === "-") {
    var sp = s.search(/\s/);
    var token = sp < 0 ? s : s.slice(0, sp);
    name = sp < 0 ? "" : s.slice(sp + 1).trim();
    flag = token.replace(/^-+/, "").toLowerCase();
  }
  return { flag: flag, name: name };
}

function grp(s, rx) { var m = s.match(rx); return m ? m[1] : null; }

function decodeEntities(s) {
  if (s == null) return null;
  s = String(s)
    .replace(/&#x([0-9a-fA-F]+);/g, function (_, h) { return String.fromCodePoint(parseInt(h, 16)); })
    .replace(/&#(\d+);/g, function (_, d) { return String.fromCodePoint(parseInt(d, 10)); })
    .replace(/&nbsp;/g, " ").replace(/&copy;/g, "©").replace(/&reg;/g, "®").replace(/&trade;/g, "™")
    .replace(/&lt;/g, "<").replace(/&gt;/g, ">").replace(/&quot;/g, '"')
    .replace(/&#39;/g, "'").replace(/&apos;/g, "'")
    .replace(/&amp;/g, "&");   // &amp; last, so "&amp;copy;" stays the literal "&copy;"
  s = s.trim();
  return s.length ? s : null;
}

function gmsPercent(level, exp) {
  if (level < 1 || level >= GMS_EXP.length) return null;
  var need = GMS_EXP[level];
  if (!need || need <= 0) return null;          // cap / no next level
  var pct = exp / need * 100.0;
  return pct < 0 ? 0 : (pct > 100 ? 100 : pct);
}

function url(tmpl, name) { return tmpl.replace("{n}", encodeURIComponent(name)); }

// The link button label, matching the built-in /rank ("Check more info on <site> ↗").
function infoLabel(site) { return "Check more info on " + site + " ↗"; }

// ---- GMS (-na / -eu): keyless nexon.com rankings -----------------------------------------------------
function doGms(server, name) {
  var base = server.base + "/" + server.code;
  var resp = httpGet(base + "?type=overall&id=weekly&reboot_index=0&character_name=" + encodeURIComponent(name));
  if (!resp.ok) { say("The GMS rankings API request failed (" + resp.status + ")."); return; }
  var data = JSON.parse(resp.body);
  var ranks = data.ranks;
  if (!ranks || !ranks.length) { say('Character "' + name + '" not found in the GMS rankings.'); return; }

  var row = null;
  for (var i = 0; i < ranks.length; i++) { if (ranks[i].isSearchTarget === true) { row = ranks[i]; break; } }
  if (!row && ranks[0].characterName &&
      ranks[0].characterName.trim().toLowerCase() === name.trim().toLowerCase()) row = ranks[0];
  if (!row) { say('Character "' + name + '" not found in the GMS rankings.'); return; }

  var level = row.level | 0;
  var wid = (typeof row.worldID === "number") ? row.worldID : -1;
  var world = GMS_WORLDS[wid] || (wid >= 0 ? "World " + wid : "?");
  var cname = row.characterName || name;
  var exp = (typeof row.exp === "number") ? row.exp : 0;

  rank({
    name: cname,
    level: level,
    job: row.jobName || "?",
    world: world,
    expPercent: gmsPercent(level, exp),
    rank: (typeof row.rank === "number") ? row.rank : 0,
    legionLevel: gmsLegion(base, wid, cname),
    imageUrl: row.characterImgURL || null,
    serverLabel: server.label,
    infoTitle: infoLabel(server.infoSite),
    infoUrl: url(server.info, cname)
  });
}

// Best-effort Legion level: only present when the looked-up name IS its account's Legion representative.
function gmsLegion(base, worldId, name) {
  if (worldId == null || worldId < 0) return 0;
  try {
    var resp = httpGet(base + "?type=legion&id=" + worldId + "&reboot_index=0&character_name=" + encodeURIComponent(name));
    if (!resp.ok) return 0;
    var ranks = JSON.parse(resp.body).ranks;
    if (!ranks || !ranks.length) return 0;
    var r = ranks[0];
    if (!r.characterName || r.characterName.trim().toLowerCase() !== name.trim().toLowerCase()) return 0;
    return (typeof r.legionLevel === "number") ? r.legionLevel : 0;
  } catch (e) { return 0; }
}

// ---- KMS / MSEA (-kr / -sea): scrape the maple.gg page + best-effort dak.gg JSON ---------------------
function doMapleGg(server, name) {
  var resp = httpGet(url(server.data, name), {
    "Accept": "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
    "Accept-Language": "en-US,en;q=0.9,ko;q=0.8"
  });
  if (resp.status >= 300 && resp.status < 400) { say('Character "' + name + '" not found on maple.gg.'); return; }
  if (!resp.ok) { say("maple.gg request failed (" + resp.status + ")."); return; }

  var p = parseMapleGg(resp.body);
  if (!p) { say('Character "' + name + '" not found on maple.gg.'); return; }

  var expPct = null, globalRank = null, legion = null;
  if (server.stats) {
    try {
      var sresp = httpGet(url(server.stats, p.name), {
        "Accept": "application/json",
        "Referer": (server.stats.indexOf("msea.dakgg.io") >= 0) ? "https://msea.maple.gg/" : "https://maple.gg/"
      });
      if (sresp.ok) {
        var sd = JSON.parse(sresp.body);
        if (sd.totalRank && typeof sd.totalRank.rank === "number" && sd.totalRank.rank > 0) globalRank = sd.totalRank.rank;
        if (sd.unionRank && typeof sd.unionRank.n4level === "number" && sd.unionRank.n4level > 0) legion = sd.unionRank.n4level;
        expPct = mostRecentExp(sd);
      }
    } catch (e) { /* best-effort: leave them null */ }
  }

  rank({
    name: p.name, level: p.level, job: p.cls, world: p.world, guild: p.guild,
    fame: p.pop, imageUrl: p.image, expPercent: expPct, rank: globalRank, legionLevel: legion,
    serverLabel: server.label, infoTitle: infoLabel(server.infoSite), infoUrl: url(server.info, p.name)
  });
}

function parseMapleGg(html) {
  var h = html.replace(/<!--[\s\S]*?-->/g, "");                 // strip React SSR comment markers
  var name = decodeEntities(grp(h, /class="nickname">([^<]*)<\/div>/));
  if (!name) return null;                                       // no nickname card ⇒ no such character

  var levelStr = grp(h, /class="level">\s*Lv\.\s*([\d,]+)/);
  var level = levelStr ? parseInt(levelStr.replace(/,/g, ""), 10) : 0;
  var cls = decodeEntities(grp(h, /class="job">[\s\S]*?<span>([^<]*)<\/span>/)) || "?";
  var world = decodeEntities(grp(h, /class="world">[\s\S]*?<span>([^<]*)<\/span>/)) || "?";
  var guild = decodeEntities(grp(h, /class="guild">[\s\S]*?<span>([^<]*)<\/span>/));

  var pop = null;
  var popText = grp(h, /class="popularity">([^<]*)<\/span>/);
  if (popText) { var dm = popText.match(/[\d,]+/); if (dm) pop = parseInt(dm[0].replace(/,/g, ""), 10); }

  var image = null;
  var tag = h.match(/<img[^>]*class="character-image"[^>]*>/);
  if (tag) { var src = tag[0].match(/\bsrc="([^"]+)"/); if (src) image = src[1]; }

  return { name: name, level: level, cls: cls, world: world, guild: guild, pop: pop, image: image };
}

// Most recent EXP-history point's %; each entry is [timestampMs, level, exp%, rawExp]; null at the cap (300).
function mostRecentExp(root) {
  var logs = root.characterExpLogs;
  if (!logs || !logs.length) return null;
  var last = logs[logs.length - 1];
  if (!last || last.length < 3 || typeof last[1] !== "number" || typeof last[2] !== "number") return null;
  if (last[1] >= 300) return null;
  return last[2];
}

// ---- TMS (-tw): the keyless maple-kit.com proxy of the Open API ---------------------------------------
function doMapleKit(server, name) {
  var resp = httpGet(url(server.data, name), { "Accept": "application/json" });
  var data;
  try { data = JSON.parse(resp.body); }
  catch (e) { say("maple-kit.com returned an unreadable response (" + resp.status + ")."); return; }

  if (data && data.error) { say(friendlyKit(data.error.name, name)); return; }
  if (!resp.ok) { say("maple-kit.com request failed (" + resp.status + ")."); return; }

  var b = data.basic;
  if (!b) { say('Character "' + name + '" not found.'); return; }

  var level = b.character_level | 0;
  var grade = null;
  var ulvl = null;
  if (data.union) {
    if (typeof data.union.union_level === "number") ulvl = data.union.union_level;
    grade = data.union.union_grade || null;
  }

  rank({
    name: b.character_name || name,
    level: level,
    job: b.character_class || "?",
    world: b.world_name || "?",
    expPercent: (level >= 300) ? null : parsePct(b.character_exp_rate),
    guild: b.character_guild_name || null,
    imageUrl: b.character_image || null,
    rank: (data.ranking && typeof data.ranking.rank === "number" && data.ranking.rank > 0) ? data.ranking.rank : null,
    fame: (data.popularity && typeof data.popularity.popularity === "number") ? data.popularity.popularity : null,
    legionLevel: ulvl,
    legionGrade: grade,
    serverLabel: server.label,
    infoTitle: infoLabel(server.infoSite),
    infoUrl: url(server.info, b.character_name || name)
  });
}

function parsePct(r) { var d = parseFloat(r); return isNaN(d) ? null : d; }

function friendlyKit(code, subject) {
  switch (code) {
    case "OPENAPI00003":
    case "OPENAPI00004": return subject ? ('Character "' + subject + '" not found.') : "Character not found.";
    case "OPENAPI00007": return "Hit the MapleStory API rate limit — try again in a moment.";
    case "OPENAPI00009": return "MapleStory data is still being prepared — try again later.";
    case "OPENAPI00010":
    case "OPENAPI00011": return "The MapleStory API is under maintenance.";
    default: return code ? ("maple-kit.com error (" + code + ").") : "maple-kit.com returned an error.";
  }
}

// ---- entry point -------------------------------------------------------------------------------------
var a = parseInput(args);
var server = SERVERS[a.flag];
if (!server) {
  say('Unknown flag "-' + a.flag + '" — use -na, -eu, -kr, -sea, -tw.');
} else if (!a.name) {
  say("Usage: /rankx [-na|-eu|-kr|-sea|-tw] <character name>");
} else if (server.kind === "gms") {
  doGms(server, a.name);
} else if (server.kind === "maplegg") {
  doMapleGg(server, a.name);
} else {
  doMapleKit(server, a.name);
}
