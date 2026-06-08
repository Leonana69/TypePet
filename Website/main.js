/* ============================================================================
   TypePet website — interactions
   - walking-pet engine that roams a faux desktop using the real sprite frames
   - pose showcase animators, animated chat & slash-command demos
   - nav, tabs, FAQ, reveal-on-scroll, copy buttons, back-to-top
   ========================================================================== */
(function () {
  "use strict";

  const SPR = "assets/sprites/";
  const reduceMotion = window.matchMedia("(prefers-reduced-motion: reduce)").matches;

  // ----- sprite frame tables (frame ids + per-frame ms) --------------------
  const POSES = {
    stand:   { frames: ["stand1_f00", "stand1_f01", "stand1_f00", "stand1_f02"], ms: 560 },
    walk:    { frames: ["walk1_f00", "walk1_f01", "walk1_f02", "walk1_f03"], ms: 150 },
    ladder:  { frames: ["ladder_f00", "ladder_f01"], ms: 220 },
    jump:    { frames: ["jump_f00", "jump_f01", "jump_f02"], ms: 150 },
    alert:   { frames: ["alert_f00", "alert_f01", "alert_f02", "alert_f01"], ms: 140 },
    heal:    { frames: ["heal_f00", "heal_f01", "heal_f02"], ms: 230 },
    sit:     { frames: ["sit_f00"], ms: 600 },
    prone:   { frames: ["prone_f00"], ms: 600 },
    fly:     { frames: ["fly_f00", "fly_f01"], ms: 260 },
    swingO1: { frames: ["swingO1_f00", "swingO1_f01", "swingO1_f02"], ms: 120 },
    stabO1:  { frames: ["stabO1_f00", "stabO1_f01"], ms: 160 },
  };
  const src = (id) => SPR + id + ".png";

  // preload every frame so animation never flickers
  Object.values(POSES).forEach((p) => p.frames.forEach((f) => { const i = new Image(); i.src = src(f); }));

  // =========================================================================
  //  Central ticker — drives walking pets + simple pose loops
  // =========================================================================
  const pets = [];   // walking pet engines
  const loops = [];  // {img, pose, idx, acc} simple frame loops
  let last = 0;

  function addLoop(img, pose) {
    const o = { img, pose, idx: 0, acc: 0, _pose: null, _src: "" };
    loops.push(o);
    return o;
  }

  function setFrame(o, id) { const s = src(id); if (o._src !== s) { o._src = s; o.img.src = s; } }

  function tick(now) {
    if (!last) last = now;
    let dt = (now - last) / 1000;
    last = now;
    if (dt > 0.1) dt = 0.1; // clamp after tab-switches

    for (const o of loops) {
      const p = POSES[o.pose] || POSES.stand;
      if (o._pose !== o.pose) { o._pose = o.pose; o.idx = 0; o.acc = 0; setFrame(o, p.frames[0]); }
      o.acc += dt * 1000;
      while (o.acc >= p.ms) { o.acc -= p.ms; o.idx = (o.idx + 1) % p.frames.length; }
      setFrame(o, p.frames[o.idx]);
    }

    for (const pet of pets) pet.update(dt);

    requestAnimationFrame(tick);
  }

  // =========================================================================
  //  Walking pet engine
  // =========================================================================
  const WALK_SPEED = 0.26;  // stage-fraction per second
  const CLIMB_SPEED = 0.34;
  const JUMP_DUR = 0.72;    // seconds

  // a believable tour across the faux desktop (feet coords as stage fractions)
  const TOUR = [
    { a: "face", dir: "R" },
    { a: "idle", t: 1100, pose: "stand", say: "hi! ✨" },
    { a: "walk", x: 0.575 },
    { a: "idle", t: 450, pose: "alert" },
    { a: "climb", y: 0.14 },
    { a: "walk", x: 0.80 },
    { a: "idle", t: 850, pose: "heal", say: "wheee~" },
    { a: "face", dir: "L" },
    { a: "walk", x: 0.60 },
    { a: "jump", x: 0.33, y: 0.56 },
    { a: "idle", t: 420, pose: "alert" },
    { a: "face", dir: "R" },
    { a: "walk", x: 0.55 },
    { a: "jump", x: 0.75, y: 0.88 },
    { a: "face", dir: "L" },
    { a: "walk", x: 0.095 },
    { a: "climb", y: 0.30 },
    { a: "walk", x: 0.38 },
    { a: "idle", t: 750, pose: "stand", say: "made it!" },
    { a: "face", dir: "L" },
    { a: "walk", x: 0.10 },
    { a: "climb", y: 0.88 },
    { a: "walk", x: 0.18 },
  ];

  function createPet(stage, petEl, imgEl, bubbleEl) {
    const pet = {
      stage, petEl, imgEl, bubbleEl,
      fx: 0.18, fy: 0.88, facing: "R",
      i: 0, elapsed: 0, jumpFrom: null,
      frameIdx: 0, frameAcc: 0, curPose: "stand", _src: "",
    };
    petEl.style.transformOrigin = "bottom center";

    function place() {
      const w = stage.clientWidth, h = stage.clientHeight;
      const s = Math.max(0.7, Math.min(1.05, w / 520));
      const bw = petEl.offsetWidth || 72, bh = petEl.offsetHeight || 70;
      const left = pet.fx * w - bw / 2;
      const top = pet.fy * h - bh;
      petEl.style.transform = `translate(${left}px, ${top}px) scale(${s})`;
    }

    function setPose(name) { if (pet.curPose !== name) { pet.curPose = name; pet.frameIdx = 0; pet.frameAcc = 0; } }

    function animateFrames(dt) {
      const p = POSES[pet.curPose] || POSES.stand;
      pet.frameAcc += dt * 1000;
      while (pet.frameAcc >= p.ms) { pet.frameAcc -= p.ms; pet.frameIdx = (pet.frameIdx + 1) % p.frames.length; }
      const id = p.frames[pet.frameIdx], s = src(id);
      if (pet._src !== s) { pet._src = s; imgEl.src = s; }
      imgEl.classList.toggle("flip", pet.facing === "R");
    }

    function say(text) {
      if (!bubbleEl) return;
      bubbleEl.textContent = text;
      petEl.classList.add("talk");
    }
    function hush() { if (bubbleEl) petEl.classList.remove("talk"); }

    function next() { pet.i = (pet.i + 1) % TOUR.length; pet.elapsed = 0; pet.jumpFrom = null; if (pet.i === 0) hush(); }

    pet.update = function (dt) {
      const step = TOUR[pet.i];
      switch (step.a) {
        case "face": pet.facing = step.dir; next(); break;
        case "idle": {
          setPose(step.pose || "stand");
          if (step.say && pet.elapsed === 0) say(step.say);
          pet.elapsed += dt * 1000;
          if (pet.elapsed >= step.t) { if (step.say) hush(); next(); }
          break;
        }
        case "walk": {
          setPose("walk");
          const dir = step.x > pet.fx ? 1 : -1;
          pet.facing = dir > 0 ? "R" : "L";
          pet.fx += dir * WALK_SPEED * dt;
          if ((dir > 0 && pet.fx >= step.x) || (dir < 0 && pet.fx <= step.x)) { pet.fx = step.x; next(); }
          break;
        }
        case "climb": {
          setPose("ladder");
          const dir = step.y > pet.fy ? 1 : -1;
          pet.fy += dir * CLIMB_SPEED * dt;
          if ((dir > 0 && pet.fy >= step.y) || (dir < 0 && pet.fy <= step.y)) { pet.fy = step.y; next(); }
          break;
        }
        case "jump": {
          setPose("jump");
          if (!pet.jumpFrom) { pet.jumpFrom = { x: pet.fx, y: pet.fy }; pet.facing = step.x >= pet.fx ? "R" : "L"; }
          pet.elapsed += dt * 1000;
          const p = Math.min(1, pet.elapsed / (JUMP_DUR * 1000));
          pet.fx = pet.jumpFrom.x + (step.x - pet.jumpFrom.x) * p;
          pet.fy = pet.jumpFrom.y + (step.y - pet.jumpFrom.y) * p - 0.12 * Math.sin(p * Math.PI);
          if (p >= 1) { pet.fx = step.x; pet.fy = step.y; next(); }
          break;
        }
      }
      animateFrames(dt);
      place();
    };

    // initial paint
    setFrame({ img: imgEl, _src: "" }, POSES.stand.frames[0]);
    place();
    return pet;
  }

  function initScene(stageId, petId, imgId, bubbleId) {
    const stage = document.getElementById(stageId);
    const petEl = document.getElementById(petId);
    const imgEl = document.getElementById(imgId);
    if (!stage || !petEl || !imgEl) return;
    const bubbleEl = bubbleId ? document.getElementById(bubbleId) : null;

    if (reduceMotion) {
      // static: stand the pet on the dock, no roaming
      petEl.style.transformOrigin = "bottom center";
      imgEl.src = src(POSES.stand.frames[0]);
      const w = stage.clientWidth, h = stage.clientHeight;
      const bw = petEl.offsetWidth || 72, bh = petEl.offsetHeight || 70;
      petEl.style.transform = `translate(${0.5 * w - bw / 2}px, ${0.88 * h - bh}px)`;
      return;
    }
    pets.push(createPet(stage, petEl, imgEl, bubbleEl));
  }

  // =========================================================================
  //  Pose showcase
  // =========================================================================
  function initPoses() {
    document.querySelectorAll(".pose-sprite").forEach((img) => {
      const pose = img.getAttribute("data-pose");
      if (!POSES[pose]) return;
      if (reduceMotion) { img.src = src(POSES[pose].frames[0]); return; }
      addLoop(img, pose);
    });
  }

  // =========================================================================
  //  Chat demo (typewriter)
  // =========================================================================
  const CHAT = [
    { u: "what's the weather in Seoul?", p: "Let me check… ☔ It's <em>18°C and raining</em> in Seoul — grab an umbrella!" },
    { u: "remind me to stretch in 30 min", p: "Done! I'll nudge you to <em>stretch</em> at 3:30 PM. ⏰" },
    { u: "do a little dance!", p: "*hops and spins* 💃 ta-da!" },
    { u: "what's a good inner ability for a bishop?", p: "Looked it up — aim for <em>Boss Damage</em> first, then cooldown skip. 📖" },
  ];
  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

  function initChat() {
    const log = document.getElementById("chatLog");
    const typed = document.getElementById("chatTyped");
    if (!log || !typed) return;

    function addMsg(cls, html) {
      const el = document.createElement("div");
      el.className = "chat__msg " + cls;
      el.innerHTML = html;
      log.appendChild(el);
      requestAnimationFrame(() => el.classList.add("show"));
      while (log.children.length > 6) log.removeChild(log.firstChild);
      log.scrollTop = log.scrollHeight;
    }

    if (reduceMotion) {
      CHAT.slice(0, 2).forEach((c) => { addMsg("chat__msg--user", c.u); addMsg("chat__msg--pet", c.p); });
      return;
    }

    let i = 0, started = false;
    async function run() {
      while (true) {
        while (document.hidden) await sleep(500); // idle while the tab is backgrounded
        const c = CHAT[i % CHAT.length];
        // type into the say-bar
        typed.textContent = "";
        for (const ch of c.u) { typed.textContent += ch; await sleep(40 + Math.random() * 45); }
        await sleep(380);
        addMsg("chat__msg--user", c.u);
        typed.textContent = "";
        await sleep(420);
        // pet "typing" indicator
        const t = document.createElement("div");
        t.className = "chat__msg chat__msg--pet show";
        t.innerHTML = '<span class="typing"><i></i><i></i><i></i></span>';
        log.appendChild(t); log.scrollTop = log.scrollHeight;
        await sleep(1050);
        log.removeChild(t);
        addMsg("chat__msg--pet", c.p);
        await sleep(2400);
        i++;
      }
    }

    const io = new IntersectionObserver((entries) => {
      if (entries[0].isIntersecting && !started) { started = true; run(); io.disconnect(); }
    }, { threshold: 0.3 });
    io.observe(log);
  }

  // =========================================================================
  //  Slash-command demo
  // =========================================================================
  // Each demo mirrors the real bundled/hub command: valid invocation, true kind, representative output.
  const CMD = {
    wave:      { type: "wave", pose: "alert", kind: "pet", bubble: "Hello! 👋 — <small>*smiles and waves*</small>" },
    roll:      { type: "roll 20", pose: "stand", kind: "script", bubble: "🎲 You rolled a <b>14</b> &nbsp;<small>(d20)</small>" },
    sf:        { type: "sf gms 16 17 160", pose: "swingO1", kind: "script", bubble: "⭐ gms 16★ → 17★ <small>(Lv160)</small> — expected meso, booms &amp; per-tap pass/drop/boom odds" },
    dailyboss: { type: "dailyboss", pose: "alert", kind: "script", bubble: "⏰ Daily reset in <b>6h 18m</b> · weekly boss reset <b>Thu</b>, in 2d 6h &nbsp;<small>(local time)</small>" },
    fortune:   { type: "fortune", pose: "heal", kind: "prompt", bubble: "🔮 “A tidy desktop today brings a tidy mind.”" },
    remind:    { type: "remind 5m tea", pose: "stand", kind: "built-in", bubble: "⏰ Okay! I'll remind you about <b>tea</b> in 5 minutes." },
  };

  function initCommands() {
    const chips = document.getElementById("cmdChips");
    const typedEl = document.getElementById("cmdTyped");
    const bubble = document.getElementById("cmdBubble");
    const petImg = document.getElementById("cmdPetImg");
    if (!chips || !typedEl || !bubble || !petImg) return;

    const cmdLoop = reduceMotion ? null : addLoop(petImg, "alert");
    let timer = null, typing = null, order = Object.keys(CMD), idx = 0;

    function select(key, fromClick) {
      const c = CMD[key];
      if (!c) return;
      chips.querySelectorAll(".chip").forEach((b) => b.classList.toggle("is-active", b.dataset.cmd === key));
      if (cmdLoop) cmdLoop.pose = c.pose; else petImg.src = src(POSES[c.pose].frames[0]);
      // typewriter the command name
      clearInterval(typing);
      typedEl.textContent = "";
      bubble.style.opacity = "0";
      let n = 0;
      typing = setInterval(() => {
        typedEl.textContent = c.type.slice(0, ++n);
        if (n >= c.type.length) {
          clearInterval(typing);
          bubble.innerHTML = c.bubble + ' <span class="kind-tag">' + c.kind + "</span>";
          bubble.style.transition = "opacity .25s ease";
          bubble.style.opacity = "1";
        }
      }, 55);
      if (fromClick) restart();
    }

    function restart() {
      clearInterval(timer);
      if (reduceMotion) return;
      timer = setInterval(() => { if (document.hidden) return; idx = (idx + 1) % order.length; select(order[idx]); }, 3400);
    }

    chips.querySelectorAll(".chip").forEach((b) =>
      b.addEventListener("click", () => { idx = order.indexOf(b.dataset.cmd); select(b.dataset.cmd, true); })
    );

    select(order[0]);
    restart();
  }

  // =========================================================================
  //  Nav, tabs, FAQ, reveal, copy, back-to-top
  // =========================================================================
  function initNav() {
    const header = document.querySelector(".site-header");
    const nav = document.getElementById("nav");
    const toggle = document.getElementById("navToggle");
    const links = document.getElementById("navLinks");

    const onScroll = () => header.classList.toggle("is-stuck", window.scrollY > 8);
    onScroll();
    window.addEventListener("scroll", onScroll, { passive: true });

    if (toggle) {
      toggle.addEventListener("click", () => {
        const open = nav.classList.toggle("is-open");
        toggle.setAttribute("aria-expanded", String(open));
      });
    }
    if (links) {
      links.addEventListener("click", (e) => {
        if (e.target.tagName === "A") { nav.classList.remove("is-open"); toggle && toggle.setAttribute("aria-expanded", "false"); }
      });
    }
    window.addEventListener("resize", () => {
      if (window.innerWidth > 900) { nav.classList.remove("is-open"); toggle && toggle.setAttribute("aria-expanded", "false"); }
    });

    // scrollspy
    const navLinks = links ? Array.from(links.querySelectorAll("a")) : [];
    const sections = navLinks.map((a) => document.querySelector(a.getAttribute("href"))).filter(Boolean);
    if (sections.length) {
      const spy = new IntersectionObserver((entries) => {
        entries.forEach((en) => {
          if (en.isIntersecting) {
            const id = "#" + en.target.id;
            navLinks.forEach((a) => a.classList.toggle("is-active", a.getAttribute("href") === id));
          }
        });
      }, { rootMargin: "-45% 0px -50% 0px" });
      sections.forEach((s) => spy.observe(s));
    }
  }

  function initReveal() {
    const items = document.querySelectorAll(".reveal");
    if (reduceMotion || !("IntersectionObserver" in window)) { items.forEach((i) => i.classList.add("in")); return; }
    const io = new IntersectionObserver((entries, obs) => {
      entries.forEach((en) => { if (en.isIntersecting) { en.target.classList.add("in"); obs.unobserve(en.target); } });
    }, { threshold: 0.12, rootMargin: "0px 0px -8% 0px" });
    items.forEach((i) => io.observe(i));
  }

  function initCopy() {
    document.querySelectorAll(".copy-btn").forEach((btn) => {
      btn.addEventListener("click", async () => {
        const el = document.getElementById(btn.dataset.copy);
        if (!el) return;
        // strip leading shell prompts for a clean paste
        const text = el.textContent.split("\n").map((l) => l.replace(/^\s*(\$|PS>)\s/, "")).join("\n").trim();
        try { await navigator.clipboard.writeText(text); }
        catch (_) {
          const ta = document.createElement("textarea");
          ta.value = text; ta.readOnly = true;
          ta.style.cssText = "position:fixed;top:0;left:0;width:1px;height:1px;opacity:0;pointer-events:none";
          document.body.appendChild(ta);
          ta.focus(); ta.setSelectionRange(0, ta.value.length);
          try { document.execCommand("copy"); } catch (e) {}
          document.body.removeChild(ta);
        }
        const old = btn.textContent; btn.textContent = "Copied!"; btn.classList.add("copied");
        setTimeout(() => { btn.textContent = old; btn.classList.remove("copied"); }, 1600);
      });
    });
  }

  function initToTop() {
    const btn = document.getElementById("toTop");
    if (!btn) return;
    window.addEventListener("scroll", () => btn.classList.toggle("show", window.scrollY > 600), { passive: true });
    btn.addEventListener("click", () => window.scrollTo({ top: 0, behavior: reduceMotion ? "auto" : "smooth" }));
  }

  // ----- boot --------------------------------------------------------------
  function boot() {
    initNav();
    initReveal();
    initCopy();
    initToTop();
    initPoses();
    initChat();
    initCommands();
    initScene("sceneStage", "heroPet", "heroPetImg", "heroBubble");
    if (!reduceMotion) requestAnimationFrame(tick);
  }

  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", boot);
  else boot();
})();
