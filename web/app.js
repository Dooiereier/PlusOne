/*
 * Juno Second Screen - tablet console client.
 *
 * Talks to the in-game mod over a single WebSocket: telemetry frames come down,
 * control commands go up. The MJPEG view feed is a separate <img> connection so
 * that video never competes with control latency.
 */
(function () {
  'use strict';

  var TOKEN = new URLSearchParams(location.search).get('t') || '';
  var qs = TOKEN ? '?t=' + encodeURIComponent(TOKEN) : '';

  var el = function (id) { return document.getElementById(id); };
  var state = {
    control: false,
    video: null,
    telemetry: null,
    lastFrameAt: 0,
    groups: [],
    videoOn: false
  };

  /* ------------------------------------------------------------ formatting */

  function fmtDistance(m) {
    if (m === null || m === undefined || !isFinite(m)) return '—';
    var a = Math.abs(m);
    if (a >= 1e9) return (m / 1e9).toFixed(2) + ' Gm';
    if (a >= 1e6) return (m / 1e6).toFixed(2) + ' Mm';
    if (a >= 1e4) return (m / 1e3).toFixed(1) + ' km';
    if (a >= 1e3) return (m / 1e3).toFixed(2) + ' km';
    return m.toFixed(0) + ' m';
  }

  function fmtSpeed(v) {
    if (v === null || v === undefined || !isFinite(v)) return '—';
    if (Math.abs(v) >= 10000) return (v / 1000).toFixed(2) + ' km/s';
    return v.toFixed(Math.abs(v) < 100 ? 1 : 0) + ' m/s';
  }

  function fmtMass(kg) {
    if (!isFinite(kg)) return '—';
    if (kg >= 1000) return (kg / 1000).toFixed(2) + ' t';
    return kg.toFixed(0) + ' kg';
  }

  function fmtClock(seconds) {
    if (!isFinite(seconds) || seconds < 0) return '—';
    if (seconds > 86400 * 999) return '—';
    var s = Math.floor(seconds % 60);
    var m = Math.floor(seconds / 60) % 60;
    var h = Math.floor(seconds / 3600) % 24;
    var d = Math.floor(seconds / 86400);
    var pad = function (n) { return (n < 10 ? '0' : '') + n; };
    return (d > 0 ? d + 'd ' : '') + pad(h) + ':' + pad(m) + ':' + pad(s);
  }

  function fmtDeg(rad, digits) {
    if (!isFinite(rad)) return '—';
    return rad.toFixed(digits === undefined ? 1 : digits) + '°';
  }

  // Dynamic pressure q = 0.5 * air density * surface speed^2, in kPa.
  // Computed client-side from telemetry already sent (airDensity, surfaceSpeed);
  // no backend change needed.
  function dynamicPressureKpa(t) {
    if (!isFinite(t.airDensity) || !isFinite(t.surfaceSpeed)) return '—';
    var qPa = 0.5 * t.airDensity * t.surfaceSpeed * t.surfaceSpeed;
    return (qPa / 1000).toFixed(2) + ' kPa';
  }

  function setText(id, value) {
    var node = el(id);
    if (node && node.textContent !== value) node.textContent = value;
  }

  /* ------------------------------------------------------------- transport */

  var socket = null;
  var retryDelay = 500;
  var frameTimes = [];

  function connect() {
    var scheme = location.protocol === 'https:' ? 'wss:' : 'ws:';
    socket = new WebSocket(scheme + '//' + location.host + '/ws' + qs);

    socket.onopen = function () {
      retryDelay = 500;
      el('conn').className = 'dot live';
    };

    socket.onmessage = function (ev) {
      var msg;
      try { msg = JSON.parse(ev.data); } catch (e) { return; }
      if (msg.type === 'hello') {
        state.control = !!msg.control;
        state.video = msg.video || null;
        applyCapabilities();
      } else if (msg.type === 'telemetry') {
        state.telemetry = msg;
        state.lastFrameAt = performance.now();
        trackFrameRate(state.lastFrameAt);
        render(msg);
      } else if (msg.type === 'mfd') {
        renderMfdList(msg.mfds || []);
        renderCameraList(msg.cameras || []);
        trackFrameVersion('mfd', msg.mfdFrameVersion);
        trackFrameVersion('main', msg.mainViewFrameVersion);
        trackFrameVersion('external', msg.externalViewFrameVersion);
        updateSignalOverlays();
      } else if (msg.type === 'toast') {
        toast(msg.text);
      }
    };

    socket.onclose = function () {
      el('conn').className = 'dot';
      frameTimes.length = 0;
      setText('rate', '—');
      setTimeout(connect, retryDelay);
      retryDelay = Math.min(retryDelay * 2, 5000);
    };

    socket.onerror = function () { try { socket.close(); } catch (e) {} };
  }

  function send(obj) {
    if (!socket || socket.readyState !== 1) return;
    socket.send(JSON.stringify(obj));
  }

  // Frames actually arriving per second - a plain, honest link indicator.
  function trackFrameRate(now) {
    frameTimes.push(now);
    while (frameTimes.length > 1 && now - frameTimes[0] > 2000) frameTimes.shift();
    if (frameTimes.length < 2) return;
    var span = (now - frameTimes[0]) / 1000;
    setText('rate', Math.round((frameTimes.length - 1) / span) + ' Hz');
  }

  var toastTimer = null;
  function toast(text) {
    var node = el('toast');
    node.textContent = text;
    node.hidden = false;
    clearTimeout(toastTimer);
    toastTimer = setTimeout(function () { node.hidden = true; }, 2200);
  }

  /* --------------------------------------------------------- capabilities */

  function applyCapabilities() {
    el('controls').classList.toggle('locked', !state.control);
    if (state.video) {
      el('videoNote').textContent = state.video.width + 'px · ' + state.video.fps + ' fps · q' + state.video.quality;
      el('videoToggle').disabled = false;
    } else {
      el('viewHint').textContent = 'Video feed disabled in the mod settings';
      el('videoToggle').disabled = true;
    }
  }

  /* -------------------------------------------------------------- rendering */

  var lastGroupSignature = '';

  function render(t) {
    if (!t.inFlight) {
      setText('craftName', 'Not in flight');
      setText('planetName', '—');
      el('conn').className = 'dot stale';
      return;
    }
    el('conn').className = 'dot live';

    setText('craftName', t.craft || 'Craft');
    setText('planetName', t.planet || '—');
    setText('met', fmtClock(t.met));

    setText('altAsl', fmtDistance(t.altAsl));
    setText('altAgl', fmtDistance(t.altAgl));
    setText('surfaceSpeed', fmtSpeed(t.surfaceSpeed));
    setText('orbitalSpeed', fmtSpeed(t.orbitalSpeed));
    setText('verticalSpeed', fmtSpeed(t.verticalSpeed));
    setText('horizontalSpeed', fmtSpeed(t.horizontalSpeed));
    setText('gforce', isFinite(t.gForce) ? t.gForce.toFixed(2) + ' g' : '—');
    setText('mach', isFinite(t.mach) ? t.mach.toFixed(2) : '—');

    setText('atPitch', fmtDeg(t.pitch));
    setText('atHeading', fmtDeg(t.heading, 0));
    setText('atRoll', fmtDeg(t.roll));
    setText('atAoa', fmtDeg(t.aoa));

    bar('barFuel', 'valFuel', t.fuel);
    bar('barMono', 'valMono', t.monoprop);
    bar('barBatt', 'valBatt', t.battery);

    setText('twr', isFinite(t.twr) ? t.twr.toFixed(2) : '—');
    setText('deltaV', isFinite(t.deltaV) ? Math.round(t.deltaV) + ' m/s' : '—');
    setText('thrust', isFinite(t.thrust) ? (t.thrust / 1000).toFixed(1) + ' kN' : '—');
    setText('mass', fmtMass(t.mass));
    setText('isp', isFinite(t.isp) ? Math.round(t.isp) + ' s' : '—');
    setText('burnTime', fmtClock(t.burnTime));
    setText('engines', (t.activeEngines || 0) + ' / RCS ' + (t.activeRcs || 0));
    setText('stage', (t.stage || 0) + ' / ' + (t.stages || 0));

    setText('airPressure', isFinite(t.airPressure) ? (t.airPressure / 100).toFixed(1) + ' hPa' : '—');
    setText('airDensity', isFinite(t.airDensity) ? t.airDensity.toFixed(4) + ' kg/m³' : '—');
    setText('dynPressure', dynamicPressureKpa(t));
    setText('lat', fmtDeg(t.latitude, 3));
    setText('lon', fmtDeg(t.longitude, 3));

    setText('apoapsis', fmtDistance(t.apoapsis));
    setText('periapsis', fmtDistance(t.periapsis));
    setText('timeToAp', fmtClock(t.timeToAp));
    setText('timeToPe', fmtClock(t.timeToPe));
    setText('apoapsisBrief', fmtDistance(t.apoapsis));
    setText('periapsisBrief', fmtDistance(t.periapsis));
    setText('timeToApBrief', fmtClock(t.timeToAp));
    setText('timeToPeBrief', fmtClock(t.timeToPe));
    setText('ecc', isFinite(t.eccentricity) ? t.eccentricity.toFixed(4) : '—');
    setText('inc', fmtDeg(t.inclination, 2));
    setText('period', fmtClock(t.period));
    setText('orbitBody', t.planet || '—');

    if (!throttleHeld) setThrottleDisplay(t.throttle);
    syncGroups(t.groups || []);
    setToggle('translation', t.translationMode);

    drawNavball(t);
    drawOrbit(t);
  }

  function bar(fillId, valueId, fraction) {
    var f = isFinite(fraction) ? Math.max(0, Math.min(1, fraction)) : 0;
    el(fillId).style.width = (f * 100).toFixed(1) + '%';
    setText(valueId, isFinite(fraction) ? Math.round(f * 100) + '%' : '—');
  }

  function setToggle(cmd, on) {
    var node = document.querySelector('[data-cmd="' + cmd + '"]');
    if (node) node.classList.toggle('on', !!on);
  }

  function syncGroups(groups) {
    var signature = groups.map(function (g) { return g.name; }).join('|');
    var row = el('groupRow');
    if (signature !== lastGroupSignature) {
      lastGroupSignature = signature;
      row.innerHTML = '';
      groups.forEach(function (g) {
        var b = document.createElement('button');
        b.className = 'ag';
        b.dataset.group = g.i;
        b.innerHTML = '<b>' + g.i + '</b><em></em>';
        b.querySelector('em').textContent = g.name || '';
        row.appendChild(b);
      });
    }
    groups.forEach(function (g) {
      var node = row.querySelector('[data-group="' + g.i + '"]');
      if (node) node.classList.toggle('on', !!g.on);
    });
  }

  /* --------------------------------------------------------------- navball */

  function drawNavball(t) {
    var canvas = el('navball');
    var ctx = canvas.getContext('2d');
    var w = canvas.width, h = canvas.height;
    var cx = w / 2, cy = h / 2, R = Math.min(w, h) / 2 - 14;

    ctx.clearRect(0, 0, w, h);
    ctx.save();
    ctx.beginPath();
    ctx.arc(cx, cy, R, 0, Math.PI * 2);
    ctx.clip();

    var pitch = t.pitch || 0;
    var roll = t.roll || 0;
    var heading = t.heading || 0;
    var pxPerDeg = R / 80; // was R / 55 — bigger divisor shows a wider pitch range for the same radius

    ctx.save();
    ctx.translate(cx, cy);
    ctx.rotate(-roll * Math.PI / 180);
    ctx.translate(0, pitch * pxPerDeg);

    ctx.fillStyle = '#1d4f7a';
    ctx.fillRect(-R * 2, -R * 4, R * 4, R * 4);
    ctx.fillStyle = '#6b4a2a';
    ctx.fillRect(-R * 2, 0, R * 4, R * 4);

    ctx.strokeStyle = 'rgba(255,255,255,0.85)';
    ctx.lineWidth = 2;
    ctx.beginPath();
    ctx.moveTo(-R * 2, 0);
    ctx.lineTo(R * 2, 0);
    ctx.stroke();

    ctx.strokeStyle = 'rgba(255,255,255,0.45)';
    ctx.fillStyle = 'rgba(255,255,255,0.7)';
    ctx.lineWidth = 1;
    ctx.font = '11px -apple-system, sans-serif';
    ctx.textAlign = 'center';
    for (var d = -80; d <= 80; d += 10) {
      if (d === 0) continue;
      var y = -d * pxPerDeg;
      var half = (d % 30 === 0) ? R * 0.32 : R * 0.16;
      ctx.beginPath();
      ctx.moveTo(-half, y);
      ctx.lineTo(half, y);
      ctx.stroke();
      if (d % 10 === 0) ctx.fillText(String(d), half + 16, y + 4);
    }
    ctx.restore();

    // Heading tape, kept well inside the rim so the circular clip cannot cut it.
    var tapeY = cy - R * 0.80;
    ctx.fillStyle = 'rgba(7,11,18,0.55)';
    ctx.fillRect(cx - R * 0.55, tapeY - 12, R * 1.10, 26);
    ctx.strokeStyle = 'rgba(216,227,242,0.55)';
    ctx.fillStyle = 'rgba(216,227,242,0.9)';
    ctx.font = '12px -apple-system, sans-serif';
    ctx.textAlign = 'center';
    for (var hd = 0; hd < 360; hd += 30) {
      var rel = ((hd - heading + 540) % 360) - 180;
      if (Math.abs(rel) > 60) continue;
      var x = cx + (rel / 60) * R * 0.50;
      ctx.beginPath();
      ctx.moveTo(x, tapeY - 10);
      ctx.lineTo(x, tapeY - 4);
      ctx.stroke();
      ctx.fillText(hd === 0 ? 'N' : hd === 90 ? 'E' : hd === 180 ? 'S' : hd === 270 ? 'W' : String(hd), x, tapeY + 8);
    }

    ctx.strokeStyle = '#4fd1e0';
    ctx.beginPath();
    ctx.moveTo(cx, tapeY - 14);
    ctx.lineTo(cx - 4, tapeY - 20);
    ctx.lineTo(cx + 4, tapeY - 20);
    ctx.closePath();
    ctx.stroke();

    drawMarker(ctx, cx, cy, R, t.cf, t.cr, t.cu, t.prograde, '#57c98a', 'pro');
    drawMarker(ctx, cx, cy, R, t.cf, t.cr, t.cu, negate(t.prograde), '#e2564d', 'retro');
    if (t.targetDir) drawMarker(ctx, cx, cy, R, t.cf, t.cr, t.cu, t.targetDir, '#f0a63c', 'target');

    ctx.restore();

    // fixed craft reticle
    ctx.strokeStyle = '#4fd1e0';
    ctx.lineWidth = 2.5;
    ctx.beginPath();
    ctx.moveTo(cx - R * 0.30, cy); ctx.lineTo(cx - R * 0.08, cy);
    ctx.moveTo(cx + R * 0.08, cy); ctx.lineTo(cx + R * 0.30, cy);
    ctx.moveTo(cx, cy - R * 0.10); ctx.lineTo(cx, cy - R * 0.02);
    ctx.stroke();

    ctx.strokeStyle = 'rgba(216,227,242,0.35)';
    ctx.lineWidth = 2;
    ctx.beginPath();
    ctx.arc(cx, cy, R, 0, Math.PI * 2);
    ctx.stroke();
  }

  function negate(v) { return v ? [-v[0], -v[1], -v[2]] : null; }

  function drawMarker(ctx, cx, cy, R, fwd, right, up, dir, color, kind) {
    if (!fwd || !right || !up || !dir) return;
    var df = dot(dir, fwd), dr = dot(dir, right), du = dot(dir, up);
    var x = cx + dr * R;
    var y = cy - du * R;
    var behind = df < 0;

    // Prograde and retrograde are always exact opposites, so exactly one of
    // them faces the camera at any moment - show only that one instead of
    // drawing both (one dimmed).
    if (behind && (kind === 'pro' || kind === 'retro')) return;

    ctx.save();
    ctx.globalAlpha = behind ? 0.3 : 1;
    ctx.strokeStyle = color;
    ctx.lineWidth = 2.5;
    ctx.beginPath();
    ctx.arc(x, y, R * 0.075, 0, Math.PI * 2);
    ctx.stroke();

    ctx.beginPath();
    if (kind === 'retro') {
      var s = R * 0.075;
      ctx.moveTo(x - s, y - s); ctx.lineTo(x + s, y + s);
      ctx.moveTo(x + s, y - s); ctx.lineTo(x - s, y + s);
    } else if (kind === 'target') {
      ctx.moveTo(x - R * 0.13, y); ctx.lineTo(x + R * 0.13, y);
      ctx.moveTo(x, y - R * 0.13); ctx.lineTo(x, y + R * 0.13);
    } else {
      ctx.moveTo(x - R * 0.15, y); ctx.lineTo(x - R * 0.075, y);
      ctx.moveTo(x + R * 0.075, y); ctx.lineTo(x + R * 0.15, y);
      ctx.moveTo(x, y - R * 0.15); ctx.lineTo(x, y - R * 0.075);
      ctx.arc(x, y, R * 0.02, 0, Math.PI * 2);
    }
    ctx.stroke();
    ctx.restore();
  }

  function dot(a, b) { return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]; }

  /* ----------------------------------------------------------- orbit plot */

  function drawOrbit(t) {
    var canvas = el('orbitCanvas');
    var ctx = canvas.getContext('2d');
    var w = canvas.width, h = canvas.height;
    ctx.clearRect(0, 0, w, h);

    var Rp = t.planetRadius;
    if (!isFinite(Rp) || Rp <= 0) return;

    var rp = t.periapsis + Rp;
    var ra = t.apoapsis + Rp;
    var suborbital = !(isFinite(ra) && ra > rp && rp > 0);
    var a = (ra + rp) / 2;
    var e = suborbital ? (isFinite(t.eccentricity) ? t.eccentricity : 0) : (ra - rp) / (ra + rp);

    var extent = suborbital ? Rp * 1.6 : Math.max(ra, Rp * 1.2) * 1.12;
    var scale = (Math.min(w, h) / 2 - 12) / extent;
    var cx = w / 2, cy = h / 2;

    // planet
    var grd = ctx.createRadialGradient(cx, cy, Rp * scale * 0.2, cx, cy, Rp * scale);
    grd.addColorStop(0, '#1b3b57');
    grd.addColorStop(1, '#0e2233');
    ctx.fillStyle = grd;
    ctx.beginPath();
    ctx.arc(cx, cy, Rp * scale, 0, Math.PI * 2);
    ctx.fill();
    ctx.strokeStyle = '#2b5877';
    ctx.lineWidth = 1;
    ctx.stroke();

    if (isFinite(t.atmosphereHeight) && t.atmosphereHeight > 0) {
      ctx.save();
      ctx.setLineDash([4, 6]);
      ctx.strokeStyle = 'rgba(79,209,224,0.35)';
      ctx.beginPath();
      ctx.arc(cx, cy, (Rp + t.atmosphereHeight) * scale, 0, Math.PI * 2);
      ctx.stroke();
      ctx.restore();
    }

    if (!suborbital) {
      var b = a * Math.sqrt(Math.max(0, 1 - e * e));
      var c = a * e;
      ctx.save();
      ctx.translate(cx - c * scale, cy);
      ctx.strokeStyle = '#4fd1e0';
      ctx.lineWidth = 1.8;
      ctx.beginPath();
      ctx.ellipse(0, 0, a * scale, b * scale, 0, 0, Math.PI * 2);
      ctx.stroke();
      ctx.restore();

      marker(ctx, cx + ra * scale, cy, '#f0a63c', 'Ap');
      marker(ctx, cx - rp * scale, cy, '#57c98a', 'Pe');

      // craft position from the conic equation, branch chosen by radial velocity
      var r = t.radius;
      if (isFinite(r) && r > 0 && e >= 0) {
        var cosNu = e > 1e-6 ? ((a * (1 - e * e) / r) - 1) / e : 1;
        cosNu = Math.max(-1, Math.min(1, cosNu));
        var nu = Math.acos(cosNu);
        if (t.verticalSpeed < 0) nu = -nu;
        var px = cx + r * Math.cos(nu) * scale;
        var py = cy - r * Math.sin(nu) * scale;
        ctx.fillStyle = '#ffffff';
        ctx.beginPath();
        ctx.arc(px, py, 5, 0, Math.PI * 2);
        ctx.fill();
      }
    } else {
      ctx.fillStyle = '#7f90a8';
      ctx.font = '14px -apple-system, sans-serif';
      ctx.textAlign = 'center';
      ctx.fillText('Suborbital', cx, cy + Rp * scale + 26);
    }

    ctx.textAlign = 'left';
  }

  function marker(ctx, x, y, color, label) {
    ctx.fillStyle = color;
    ctx.beginPath();
    ctx.arc(x, y, 4, 0, Math.PI * 2);
    ctx.fill();
    ctx.font = '12px -apple-system, sans-serif';
    ctx.fillText(label, x + 8, y + 4);
  }

  /* ------------------------------------------------------------------- mfd */

  var mfdList = [];
  var mfdSelectedPart = null;
  var mfdSelectSignature = '';
  var mfdTabActive = false;
  var viewTabActive = false;

  function renderMfdList(mfds) {
    mfdList = mfds;

    var signature = mfds.map(function (m) { return m.part; }).join('|');
    var list = el('mfdList');
    if (signature !== mfdSelectSignature) {
      mfdSelectSignature = signature;
      list.innerHTML = '';

      if (mfds.length === 0) {
        var empty = document.createElement('div');
        empty.className = 'mfdlistempty';
        empty.textContent = 'No MFDs';
        list.appendChild(empty);
      }

      mfds.forEach(function (m) {
        var button = document.createElement('button');
        button.type = 'button';
        button.textContent = m.part;
        button.dataset.part = m.part;
        list.appendChild(button);
      });

      if (mfdSelectedPart === null || !mfds.some(function (m) { return m.part === mfdSelectedPart; })) {
        mfdSelectedPart = mfds.length > 0 ? mfds[0].part : null;
        refreshMfdFeed();
      }

      updateMfdListActiveState();
    }

    drawMfd();
  }

  function updateMfdListActiveState() {
    document.querySelectorAll('#mfdList button').forEach(function (button) {
      button.classList.toggle('active', button.dataset.part === mfdSelectedPart);
    });
  }

  function drawMfd() {
    var empty = el('mfdEmpty');
    if (mfdList.length === 0) {
      empty.hidden = false;
      empty.textContent = 'No MFD found on this craft';
      el('mfdFeed').classList.remove('on');
    } else {
      empty.hidden = true;
    }
  }

  // (Re)points the MFD <img> at the currently selected part's MJPEG stream.
  // Only actually connects while the MFD tab is visible, same as the View
  // tab's feed, so an unwatched MFD doesn't keep a camera/stream running
  // server-side.
  function refreshMfdFeed() {
    var img = el('mfdFeed');
    if (!mfdTabActive || !mfdSelectedPart) {
      img.removeAttribute('src');
      img.classList.remove('on');
      return;
    }

    var params = new URLSearchParams();
    params.set('part', mfdSelectedPart);
    if (TOKEN) params.set('t', TOKEN);
    params.set('r', Date.now());
    img = reconnectImg(img, '/mfd.mjpg?' + params.toString());
    img.classList.add('on');
    attachMfdFeedHandlers(img);

    // Starts a fresh grace period for this specific target rather than
    // judging staleness against whatever frameVersion last did (possibly
    // minutes ago, before anyone had even opened this tab) - otherwise a
    // feed that's never been watched yet reads as instantly "stale".
    resetFrameHealth('mfd');
  }

  /* ---------------------------------------------------------------- input */

  var throttleHeld = false;

  function setThrottleDisplay(v) {
    var f = Math.max(0, Math.min(1, isFinite(v) ? v : 0));
    el('throttleFill').style.height = (f * 100).toFixed(1) + '%';
    setText('throttleValue', Math.round(f * 100) + '%');
  }

  function bindThrottle() {
    var slider = el('throttleSlider');
    var lastSent = 0;

    function valueFromEvent(ev) {
      var rect = slider.getBoundingClientRect();
      return Math.max(0, Math.min(1, 1 - (ev.clientY - rect.top) / rect.height));
    }

    function push(ev) {
      var v = valueFromEvent(ev);
      setThrottleDisplay(v);
      var now = performance.now();
      if (now - lastSent > 45) {
        lastSent = now;
        send({ cmd: 'throttle', v: v });
      }
      return v;
    }

    slider.addEventListener('pointerdown', function (ev) {
      throttleHeld = true;
      slider.setPointerCapture(ev.pointerId);
      push(ev);
      ev.preventDefault();
    });
    slider.addEventListener('pointermove', function (ev) {
      if (throttleHeld) push(ev);
    });
    function release(ev) {
      if (!throttleHeld) return;
      throttleHeld = false;
      send({ cmd: 'throttle', v: valueFromEvent(ev) });
    }
    slider.addEventListener('pointerup', release);
    slider.addEventListener('pointercancel', release);
  }

  function bindButtons() {
    el('stageButton').addEventListener('pointerdown', function (ev) {
      ev.preventDefault();
      send({ cmd: 'stage' });
      if (navigator.vibrate) navigator.vibrate(15);
    });

    el('groupRow').addEventListener('pointerdown', function (ev) {
      var button = ev.target.closest('.ag');
      if (!button) return;
      ev.preventDefault();
      var on = !button.classList.contains('on');
      button.classList.toggle('on', on);
      send({ cmd: 'ag', i: Number(button.dataset.group), on: on });
    });

    document.querySelectorAll('[data-cmd]').forEach(function (button) {
      var cmd = button.dataset.cmd;
      if (cmd === 'brake') {
        button.addEventListener('pointerdown', function (ev) { ev.preventDefault(); send({ cmd: 'brake', v: 1 }); });
        button.addEventListener('pointerup', function () { send({ cmd: 'brake', v: 0 }); });
        button.addEventListener('pointercancel', function () { send({ cmd: 'brake', v: 0 }); });
        return;
      }
      button.addEventListener('pointerdown', function (ev) {
        ev.preventDefault();
        if (cmd === 'translation') send({ cmd: 'translation' });
      });
    });

    document.querySelectorAll('[data-lock]').forEach(function (button) {
      button.addEventListener('pointerdown', function (ev) {
        ev.preventDefault();
        send({ cmd: 'lock', mode: button.dataset.lock });
      });
    });
  }

  function bindTabs() {
    el('tabs').addEventListener('click', function (ev) {
      var button = ev.target.closest('button');
      if (!button) return;
      document.querySelectorAll('#tabs button').forEach(function (b) { b.classList.toggle('active', b === button); });
      document.querySelectorAll('.panel-group').forEach(function (panel) {
        panel.hidden = panel.dataset.panel !== button.dataset.tab;
      });

      var tab = button.dataset.tab;

      var wasMfdActive = mfdTabActive;
      mfdTabActive = tab === 'mfd';
      if (mfdTabActive !== wasMfdActive) refreshMfdFeed();

      // Disconnects in the background on leaving the tab (also tells the
      // server to drop any connection still open, since switching targets
      // within a tab already triggers cleanup server-side, but leaving the
      // tab abandons a connection without ever opening a new one of that
      // same stream, so nothing would otherwise notice it's done) and
      // silently reconnects on returning, same as the MFD tab above -
      // "watching" is a standing choice (the toggle button's state), not
      // something switching tabs should turn off.
      var wasViewActive = viewTabActive;
      viewTabActive = tab === 'view';
      if (viewTabActive !== wasViewActive) refreshViewFeed();

      // The MFD/Cameras screens want the room more than the controls do -
      // shrink the bottom bar on those tabs, full size everywhere else.
      el('controls').classList.toggle('compact', tab === 'mfd' || tab === 'view');
    });
  }

  // Safari (particularly iOS) has a long-standing bug where reassigning
  // .src on an <img> displaying a multipart/x-mixed-replace (MJPEG) stream
  // does not reliably abort the old in-flight request - the old connection
  // can linger open from the browser's own point of view even as a new one
  // is opened for the new src. Enough target switches and the browser's own
  // per-origin connection limit gets exhausted, so further switches (and
  // even the feed itself) just silently stop doing anything. Replacing the
  // whole <img> element - rather than just its src - forces the browser to
  // actually tear down the old element's network resources. The new element
  // keeps the same id (and event listeners are re-attached by the caller),
  // so later el(id) lookups keep working transparently.
  function reconnectImg(img, url) {
    var fresh = document.createElement('img');
    fresh.id = img.id;
    fresh.alt = img.alt || '';
    fresh.className = img.className;
    img.replaceWith(fresh);
    fresh.src = url;
    return fresh;
  }

  function attachMfdFeedHandlers(img) {
    img.addEventListener('error', function () {
      if (mfdTabActive) el('mfdEmpty').textContent = 'feed interrupted';
    });
    img.addEventListener('click', function (ev) {
      if (!mfdSelectedPart) return;
      var uv = imageClickToUv(ev.target, ev.clientX, ev.clientY);
      if (!uv) return;
      send({ cmd: 'mfdClick', part: mfdSelectedPart, u: uv.u, v: uv.v });
    });
  }

  function bindMfd() {
    el('mfdList').addEventListener('click', function (ev) {
      var button = ev.target.closest('button');
      if (!button) return;
      mfdSelectedPart = button.dataset.part;
      updateMfdListActiveState();
      refreshMfdFeed();
    });
    attachMfdFeedHandlers(el('mfdFeed'));
  }

  // Converts a click on an <img> using object-fit:contain into a normalized
  // (u, v) position within the actual rendered image content (0,0 = top-left,
  // 1,1 = bottom-right), ignoring the letterbox/pillarbox bars object-fit
  // adds when the image's aspect ratio doesn't match its box. Returns null if
  // the click landed in a letterbox bar rather than on the image itself.
  function imageClickToUv(img, clientX, clientY) {
    if (!img.naturalWidth || !img.naturalHeight) return null;

    var box = img.getBoundingClientRect();
    var boxAspect = box.width / box.height;
    var contentAspect = img.naturalWidth / img.naturalHeight;

    var contentWidth, contentHeight;
    if (contentAspect > boxAspect) {
      contentWidth = box.width;
      contentHeight = box.width / contentAspect;
    } else {
      contentHeight = box.height;
      contentWidth = box.height * contentAspect;
    }

    var offsetX = (box.width - contentWidth) / 2;
    var offsetY = (box.height - contentHeight) / 2;
    var x = clientX - box.left - offsetX;
    var y = clientY - box.top - offsetY;

    if (x < 0 || y < 0 || x > contentWidth || y > contentHeight) return null;

    return { u: x / contentWidth, v: y / contentHeight };
  }

  var cameraListSignature = '';

  // Populates the View tab's camera picker with the craft's camera-vantage
  // parts (nose cam, docking cam, etc.), alongside the always-present "Main
  // view" option (the existing full-screen mirror feed).
  function renderCameraList(cameras) {
    var signature = cameras.join('|');
    if (signature === cameraListSignature) return;
    cameraListSignature = signature;

    var select = el('viewCameraSelect');
    var previous = select.value;
    select.innerHTML = '<option value="">Main view</option>';
    cameras.forEach(function (name) {
      var opt = document.createElement('option');
      opt.value = name;
      opt.textContent = name;
      select.appendChild(opt);
    });
    select.value = cameras.indexOf(previous) !== -1 ? previous : '';
  }

  function viewFeedUrl(cameraName) {
    var params = new URLSearchParams();
    if (cameraName) params.set('camera', cameraName);
    if (TOKEN) params.set('t', TOKEN);
    params.set('r', Date.now());
    var path = cameraName ? '/camview.mjpg' : '/stream.mjpg';
    return path + '?' + params.toString();
  }

  function attachVideoFeedHandlers(img) {
    img.addEventListener('error', function () {
      if (state.videoOn) el('videoNote').textContent = 'feed interrupted';
    });
  }

  // Reconnects the View tab's <img> at the given camera (see reconnectImg
  // for why this replaces the element instead of just its src).
  function reconnectVideoFeed(cameraName) {
    var img = reconnectImg(el('videoFeed'), viewFeedUrl(cameraName));
    img.classList.add('on');
    attachVideoFeedHandlers(img);
    resetFrameHealth(cameraName ? 'external' : 'main'); // fresh grace period - see refreshMfdFeed for why.
  }

  // (Re)points the View tab's <img> based on whether the user wants to be
  // watching (state.videoOn, the toggle button's standing state) AND the
  // tab is actually visible right now (viewTabActive) - same split as
  // refreshMfdFeed/mfdTabActive. Called both when that combination changes
  // (toggling the button, switching tabs) and when picking a different
  // camera while already watching.
  function refreshViewFeed() {
    if (!viewTabActive || !state.videoOn) {
      var img = el('videoFeed');
      img.removeAttribute('src');
      img.classList.remove('on');
      el('viewSignalLost').hidden = true;
      send({ cmd: 'stopViewFeed' });
      return;
    }

    reconnectVideoFeed(el('viewCameraSelect').value);
  }

  function bindVideo() {
    var select = el('viewCameraSelect');

    el('videoToggle').addEventListener('click', function () {
      state.videoOn = !state.videoOn;
      el('viewHint').hidden = state.videoOn;
      el('viewHint').textContent = 'Video feed off';
      el('videoToggle').textContent = state.videoOn ? 'Stop feed' : 'Start feed';
      refreshViewFeed();
    });
    select.addEventListener('change', refreshViewFeed);
    attachVideoFeedHandlers(el('videoFeed'));
  }

  /* --------------------------------------------------------------- staleness */

  setInterval(function () {
    if (!state.lastFrameAt) return;
    if (performance.now() - state.lastFrameAt > 2000 && socket && socket.readyState === 1) {
      el('conn').className = 'dot stale';
    }
  }, 1000);

  // Per-feed frame-version counters, pushed alongside the regular "mfd" WS
  // message. A counter that stops incrementing means that specific capture
  // pipeline has stalled server-side - distinct from (and not caught by) the
  // WebSocket/telemetry connection itself still looking perfectly healthy,
  // which is exactly what a stuck MFD/camera coroutine looks like.
  var SIGNAL_TIMEOUT_MS = 4000;
  var frameHealth = {
    mfd: { version: -1, at: 0 },
    main: { version: -1, at: 0 },
    external: { version: -1, at: 0 }
  };

  function trackFrameVersion(kind, version) {
    var h = frameHealth[kind];
    if (version !== undefined && version !== h.version) {
      h.version = version;
      h.at = performance.now();
    }
  }

  // Called whenever the user (re)starts watching a feed, so staleness is
  // judged from that moment rather than from whenever frameVersion last
  // happened to change - which, for a feed nobody had opened yet, could be
  // minutes in the past (or never), reading as instantly "stale" otherwise.
  function resetFrameHealth(kind) {
    frameHealth[kind] = { version: -1, at: 0 };
  }

  function isFeedStale(kind) {
    var h = frameHealth[kind];
    if (h.at === 0) return false; // no data yet - not evidence of a stall
    return performance.now() - h.at > SIGNAL_TIMEOUT_MS;
  }

  function updateSignalOverlays() {
    var mfdStale = mfdTabActive && !!mfdSelectedPart && isFeedStale('mfd');
    el('mfdSignalLost').hidden = !mfdStale;

    var viewSelect = el('viewCameraSelect');
    var viewKind = viewSelect && viewSelect.value ? 'external' : 'main';
    var viewStale = viewTabActive && state.videoOn && isFeedStale(viewKind);
    el('viewSignalLost').hidden = !viewStale;
  }

  setInterval(updateSignalOverlays, 1000);

  // Mobile browsers throttle/pause a backgrounded tab's long-lived MJPEG
  // connections (screen lock, app switch, etc.), and don't reliably resume
  // rendering new multipart frames on their own once the tab is foregrounded
  // again - the server keeps happily producing frames the whole time (no
  // "loss of signal", frameVersion never stalls), but the <img> just never
  // updates again until something opens a fresh connection. So force one
  // here rather than waiting for a user to notice and manually refresh.
  document.addEventListener('visibilitychange', function () {
    if (document.visibilityState !== 'visible') return;
    if (mfdTabActive && mfdSelectedPart) refreshMfdFeed();
    if (viewTabActive && state.videoOn) reconnectVideoFeed(el('viewCameraSelect').value);
  });

  document.addEventListener('gesturestart', function (ev) { ev.preventDefault(); });
  document.addEventListener('dblclick', function (ev) { ev.preventDefault(); });

  bindThrottle();
  bindButtons();
  bindTabs();
  bindMfd();
  bindVideo();
  connect();
})();
