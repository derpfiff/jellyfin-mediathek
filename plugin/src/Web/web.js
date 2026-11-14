/* Mediathek – Seite für Jellyfins eigene Weboberfläche.
   Der Kanal „Mediathek“ ist in Jellyfin eine Ordnerliste: ohne Suchfeld, mit hochkant beschnittenen Karten und ohne
   Download. Dieses Skript legt auf die oberste Ebene des Kanals eine eigene Seite: Suche, die Reihen der Mediathek, Serien
   mit Staffeln, Abspielen per Klick und Herunterladen im Browser. Die Live-Sender bleiben, wo Jellyfin sie zeigt: im
   Bereich „Live TV“.
   Ist das Theme „Cinema Glass“ installiert, bleibt dieses Skript untätig; das Theme bringt dieselbe Seite in seinem Stil mit. */
(function () {
    'use strict';
    if (window.__mediathekWeb) return;
    window.__mediathekWeb = true;

    var state = { probed: false, ok: false, channelId: null, rows: [], canDownload: false, seq: 0 };
    function api() { return window.ApiClient; }
    function ready() { var a = api(); return !!(a && typeof a.getCurrentUserId === 'function' && a.getCurrentUserId() && a.accessToken()); }
    function themed() { return !!window.__cinemaGlass || document.documentElement.classList.contains('atv-js'); }
    function en() { return /^en/i.test(document.documentElement.lang || navigator.language || 'de'); }
    function t(de, e) { return en() ? e : de; }
    function el(tag, cls, text) { var e = document.createElement(tag); if (cls) e.className = cls; if (text != null) e.textContent = text; return e; }
    function get(path, query) { var a = api(); return a.getJSON(a.getUrl(path, query || {})); }
    function img(item, width) {
        var tag = item.ImageTags && item.ImageTags.Primary;
        return tag ? api().getScaledImageUrl(item.Id, { type: 'Primary', maxWidth: width, tag: tag, quality: 85 }) : '';
    }
    function minutes(ticks) { var m = Math.round(ticks / 6e8); return m >= 60 ? Math.floor(m / 60) + ' h ' + (m % 60) + ' min' : m + ' min'; }

    function probe() {
        if (state.probed || !ready()) return;
        state.probed = true;
        var a = api(), uid = a.getCurrentUserId();
        get('Mediathek/home').then(function (d) {
            state.rows = (d && d.rows) || [];
            return get('Channels', { userId: uid });
        }).then(function (r) {
            var c = ((r && r.Items) || []).filter(function (i) { return i.Name === 'Mediathek'; })[0];
            state.channelId = c ? c.Id : null; state.ok = !!c;
            return a.getCurrentUser();
        }).then(function (u) {
            state.canDownload = !!(u && u.Policy && u.Policy.EnableContentDownloading);
            refresh();
        }).catch(function () { state.ok = false; });
    }

    function isFolder(item) { return item.Type === 'Series' || item.Type === 'Season' || !!item.IsFolder; }

    // Abspielen über Jellyfins eigene Karten-Logik: ein unsichtbarer Knopf in einer Kartenliste der Seite.
    function play(item, page) {
        var host = (page || document).querySelector('[is="emby-itemscontainer"]') || document.querySelector('[is="emby-itemscontainer"]');
        var a = api(), ud = item.UserData || {};
        var ticks = item.Type !== 'TvChannel' && !ud.Played && ud.PlaybackPositionTicks > 0 ? Math.floor(ud.PlaybackPositionTicks) : 0;
        if (!host) { location.hash = '#/details?id=' + item.Id + '&serverId=' + a.serverId(); return; }
        var b = el('button', 'itemAction'); b.type = 'button'; b.style.display = 'none';
        b.setAttribute('data-action', ticks > 0 ? 'resume' : 'play'); b.setAttribute('data-id', item.Id); b.setAttribute('data-serverid', a.serverId());
        b.setAttribute('data-type', item.Type || 'Video'); b.setAttribute('data-mediatype', 'Video'); b.setAttribute('data-isfolder', 'false');
        b.setAttribute('data-positionticks', String(ticks));
        host.appendChild(b); b.click(); b.remove();
    }

    function download(item, button) {
        button.disabled = true;
        get('Mediathek/download', { id: item.Id }).then(function (d) {
            if (!d || !d.path) throw new Error('no source');
            var name = String(item.__series && String(item.Name).indexOf(item.__series) < 0 ? item.__series + (item.ParentIndexNumber != null ? ' S' + item.ParentIndexNumber : '') + ' – ' + item.Name : item.Name || 'video')
                .replace(/[\\/:*?"<>|]+/g, ' ').trim() + '.mp4';
            var link = el('a'); link.href = api().getUrl(d.path); link.download = name; link.style.display = 'none';
            document.body.appendChild(link); link.click(); setTimeout(function () { link.remove(); }, 1000);
        }).catch(function () { window.alert(t('Dieser Titel lässt sich nicht herunterladen.', 'This title cannot be downloaded.')); })
            .then(function () { button.disabled = false; });
    }

    function card(item, hub) {
        var folder = isFolder(item);
        var b = el('div', 'mth-card'); b.tabIndex = 0; b.setAttribute('role', 'button');
        var pic = el('div', 'mth-pic'), src = img(item, 480);
        if (src) { var im = el('img'); im.loading = 'lazy'; im.alt = ''; im.src = src; pic.appendChild(im); } else pic.appendChild(el('span', 'mth-blank', item.Name));
        var ud = item.UserData || {};
        if (!folder && item.RunTimeTicks) pic.appendChild(el('span', 'mth-len', minutes(item.RunTimeTicks)));
        if (!folder && !ud.Played && ud.PlaybackPositionTicks > 0 && item.RunTimeTicks > 0) {
            var bar = el('span', 'mth-bar'), fill = el('i'); fill.style.width = Math.min(100, ud.PlaybackPositionTicks / item.RunTimeTicks * 100).toFixed(1) + '%'; bar.appendChild(fill); pic.appendChild(bar);
        }
        b.appendChild(pic);
        b.appendChild(el('div', 'mth-name', item.Name));
        var sub = item.Type === 'Series' ? t('Serie', 'Series') : (item.IndexNumber != null ? (item.ParentIndexNumber != null ? t('Staffel ', 'Season ') + item.ParentIndexNumber + ' · ' : '') + t('Folge ', 'Episode ') + item.IndexNumber : (item.ProductionYear ? String(item.ProductionYear) : ''));
        if (sub) b.appendChild(el('div', 'mth-sub', sub));
        if (!folder && state.canDownload) {
            var dl = el('button', 'mth-dl', '⤓'); dl.type = 'button'; dl.title = t('Herunterladen', 'Download');
            dl.addEventListener('click', function (e) { e.stopPropagation(); download(item, dl); });
            pic.appendChild(dl);
        }
        var open = function () { if (folder) series(item, hub); else play(item, hub.closest('.page, .mainAnimatedPage')); };
        b.addEventListener('click', open);
        b.addEventListener('keydown', function (e) { if (e.key === 'Enter') open(); });
        return b;
    }

    function shelf(title) {
        var sec = el('section', 'mth-shelf');
        sec.appendChild(el('h2', 'mth-title', title));
        var row = el('div', 'mth-row'); sec.appendChild(row);
        return { root: sec, row: row };
    }

    function order(x, y) {
        var a1 = x.ParentIndexNumber || 0, b1 = y.ParentIndexNumber || 0; if (a1 !== b1) return a1 - b1;
        if (x.IndexNumber != null && y.IndexNumber != null && x.IndexNumber !== y.IndexNumber) return x.IndexNumber - y.IndexNumber;
        return String(y.PremiereDate || '').localeCompare(String(x.PremiereDate || ''));
    }

    // Serie: Beschreibung, Staffeln zur Wahl (die neueste ist gewählt), Folgen als Karten.
    function series(item, hub) {
        var d = hub.detail; d.textContent = ''; hub.classList.add('is-detail'); window.scrollTo(0, 0);
        var back = el('button', 'mth-btn', '‹ ' + t('Zurück', 'Back')); back.type = 'button';
        back.addEventListener('click', function () { hub.classList.remove('is-detail'); d.textContent = ''; });
        d.appendChild(back);
        d.appendChild(el('h1', 'mth-h1', item.Name));
        if (item.Overview) d.appendChild(el('p', 'mth-text', item.Overview));
        var tabs = el('div', 'mth-tabs'), grid = el('div', 'mth-grid'), hint = el('p', 'mth-hint');
        d.appendChild(tabs); d.appendChild(grid); d.appendChild(hint);
        function show(list) {
            grid.textContent = ''; hint.textContent = list.length ? '' : t('Keine Folgen verfügbar.', 'No episodes available.');
            list.slice().sort(order).forEach(function (ep) { ep.__series = item.Name; grid.appendChild(card(ep, hub)); });
        }
        function fail() { grid.textContent = ''; hint.textContent = t('Die Folgen ließen sich nicht laden.', 'Could not load episodes.'); }
        hint.textContent = t('Lädt …', 'Loading …');
        get('Mediathek/children', { id: item.Id }).then(function (r) {
            var kids = (r && r.Items) || [], seasons = kids.filter(isFolder);
            if (!seasons.length) { show(kids); return; }
            seasons.sort(function (x, y) { return (x.IndexNumber || 0) - (y.IndexNumber || 0); });
            seasons.forEach(function (season, i) {
                var b = el('button', 'mth-tab', season.Name); b.type = 'button';
                b.addEventListener('click', function () {
                    [].forEach.call(tabs.children, function (o) { o.classList.toggle('is-on', o === b); });
                    grid.textContent = ''; hint.textContent = t('Lädt …', 'Loading …');
                    var tries = 0;
                    (function load() {
                        get('Mediathek/children', { id: season.Id }).then(function (e) {
                            if (!b.classList.contains('is-on')) return;
                            var eps = (e && e.Items) || [];
                            if (!eps.length && tries++ < 8) { setTimeout(load, 1200); return; }
                            show(eps);
                        }).catch(fail);
                    })();
                });
                tabs.appendChild(b);
                if (i === seasons.length - 1) b.click();
            });
        }).catch(fail);
    }

    function search(hub, text) {
        var q = text.trim(), seq = ++state.seq;
        hub.classList.toggle('is-searching', q.length >= 2);
        hub.results.textContent = '';
        if (q.length < 2) return;
        hub.results.appendChild(el('p', 'mth-hint', t('Sucht …', 'Searching …')));
        get('Mediathek/search', { q: q }).then(function (r) {
            if (seq !== state.seq) return;
            var items = (r && r.Items) || [];
            hub.results.textContent = '';
            if (!items.length) { hub.results.appendChild(el('p', 'mth-hint', t('Nichts gefunden für „' + q + '“.', 'Nothing found for “' + q + '”.'))); return; }
            var grid = el('div', 'mth-grid'); items.forEach(function (it) { grid.appendChild(card(it, hub)); }); hub.results.appendChild(grid);
        }).catch(function () {
            if (seq !== state.seq) return;
            hub.results.textContent = ''; hub.results.appendChild(el('p', 'mth-hint', t('Die Suche ist gerade nicht erreichbar.', 'Search is unavailable right now.')));
        });
    }

    function loadRows(hub) {
        var queue = state.rows.slice(0, 12), running = 0;
        (function next() {
            if (!hub.isConnected || !queue.length || running >= 2) return;
            var row = queue.shift(), s = shelf(row.title); hub.rows.appendChild(s.root); running++;
            get('Mediathek/row', { id: row.id }).then(function (r) {
                var items = (r && r.Items) || [];
                if (!items.length) { s.root.remove(); return; }
                items.forEach(function (it) { s.row.appendChild(card(it, hub)); });
            }).catch(function () { s.root.remove(); }).then(function () { running--; next(); });
            next();
        })();
    }

    function mount(page) {
        var hub = el('div', 'mth-hub');
        var bar = el('div', 'mth-search'), input = el('input'); input.type = 'search'; input.placeholder = t('Mediathek durchsuchen', 'Search the Mediathek'); input.autocomplete = 'off';
        bar.appendChild(input); hub.appendChild(bar);
        var timer = null;
        input.addEventListener('input', function () { clearTimeout(timer); timer = setTimeout(function () { search(hub, input.value); }, 500); });
        input.addEventListener('keydown', function (e) { if (e.key === 'Enter') { clearTimeout(timer); search(hub, input.value); } e.stopPropagation(); });
        hub.results = el('div', 'mth-results'); hub.appendChild(hub.results);
        hub.body = el('div', 'mth-body'); hub.appendChild(hub.body);
        var resume = shelf(t('Weiterschauen', 'Continue watching')); resume.root.hidden = true; hub.body.appendChild(resume.root);
        hub.rows = el('div'); hub.body.appendChild(hub.rows);
        hub.detail = el('div', 'mth-detail'); hub.appendChild(hub.detail);
        page.appendChild(hub); page.classList.add('mth-on'); page.__mth = hub;
        get('Mediathek/resume').then(function (r) {
            var items = (r && r.Items) || [];
            items.forEach(function (it) { resume.row.appendChild(card(it, hub)); });
            resume.root.hidden = !items.length;
        }).catch(function () { /* dann eben nicht */ });
        loadRows(hub);
    }

    function refresh() {
        var pages = document.querySelectorAll('.mainAnimatedPage');
        if (themed()) {   // das Theme übernimmt: aufräumen, falls schon etwas steht
            [].forEach.call(pages, function (p) { if (p.__mth) { p.__mth.remove(); p.__mth = null; p.classList.remove('mth-on'); } });
            return;
        }
        probe();
        if (!state.ok) return;
        var onHub = location.hash.indexOf('#/list') === 0 && location.hash.indexOf('parentId=' + state.channelId) >= 0;
        [].forEach.call(pages, function (p) {
            var visible = !p.classList.contains('hide') && p.getClientRects().length > 0;   // (offsetParent taugt nicht: die Seite ist am Handy fest positioniert)
            if (visible && onHub) { if (!p.__mth || !p.__mth.isConnected) mount(p); }
            else if (visible && p.__mth) { p.__mth.remove(); p.__mth = null; p.classList.remove('mth-on'); }
        });
    }

    var pending = null;
    function schedule() { clearTimeout(pending); pending = setTimeout(refresh, 150); }
    document.addEventListener('viewshow', schedule, true);
    window.addEventListener('hashchange', schedule);
    var wait = setInterval(function () { if (ready()) { clearInterval(wait); schedule(); } }, 400);
    setTimeout(function () { clearInterval(wait); }, 120000);
    // Jellyfin baut Seiten nach und nach auf und meldet nicht jeden Wechsel: gelegentlich nachsehen kostet nichts.
    setInterval(function () { if (!document.hidden) refresh(); }, 1500);
})();
