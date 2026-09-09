/**
 * SubSync — Jellyfin Client Injection Script
 *
 * Adds "Sync Subtitles" to the video detail page's "More" dropdown menu.
 */
(function () {
    'use strict';

    var SYNC_BASE = '/SubSync';

    function log(msg) {
        console.log('[SubSync] ' + msg);
    }

    function getAccessToken() {
        if (typeof ApiClient !== 'undefined' && ApiClient.accessToken) {
            return ApiClient.accessToken();
        }
        return '';
    }

    function getItemIdFromHash() {
        var hash = location.hash || '';
        var match = hash.match(/[?&]id=([0-9a-fA-F-]+)/);
        return match ? match[1] : null;
    }

    function fetchSubtitles(itemId) {
        return fetch(SYNC_BASE + '/Subtitles/' + itemId, {
            headers: { 'X-Emby-Token': getAccessToken() }
        }).then(function (r) {
            if (!r.ok) throw new Error('Failed to fetch subtitles: ' + r.status);
            return r.json();
        });
    }

    // Item id of the card/row whose menu button was last clicked (works on
    // home rows, library rows and episode lists — not just detail pages).
    var lastMenuCardId = null;
    var lastMenuCardTime = 0;
    var itemTypeCache = {};

    document.addEventListener('click', function (e) {
        var target = e.target;
        if (!target || !target.closest) return;
        var btn = target.closest('[data-action="menu"], [data-action="openmenu"], .itemActionButton, button[data-role="menu"]');
        if (!btn) return;
        var holder = btn.closest('[data-id]');
        if (holder) {
            lastMenuCardId = holder.getAttribute('data-id');
            lastMenuCardTime = Date.now();
        }
    }, true);

    // True → video (movie/episode); False → container (series/season/folder);
    // null → couldn't determine (e.g. offline). Cached per item for the session.
    function canSyncItem(itemId) {
        if (Object.prototype.hasOwnProperty.call(itemTypeCache, itemId)) {
            return Promise.resolve(itemTypeCache[itemId]);
        }
        var uid = (typeof ApiClient !== 'undefined' && ApiClient.getCurrentUserId) ? ApiClient.getCurrentUserId() : '';
        var path = uid ? 'Users/' + uid + '/Items/' + itemId : 'Items/' + itemId;
        var url = (typeof ApiClient !== 'undefined' && ApiClient.getUrl) ? ApiClient.getUrl(path) : path;
        return fetch(url, {
            headers: { 'X-Emby-Token': getAccessToken(), 'Accept': 'application/json' }
        }).then(function (r) {
            if (!r.ok) throw new Error('item lookup failed');
            return r.json();
        }).then(function (item) {
            var ok = (item.MediaType === 'Video') || (item.Type === 'Movie') || (item.Type === 'Episode') || (item.Type === 'Video');
            itemTypeCache[itemId] = ok;
            if (Object.keys(itemTypeCache).length > 300) itemTypeCache = {};
            return ok;
        }).catch(function () {
            return null; // cannot determine — caller decides fallback
        });
    }

    function startSync(itemId, subtitleIndex) {
        return fetch(SYNC_BASE + '/Sync', {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json',
                'X-Emby-Token': getAccessToken()
            },
            body: JSON.stringify({ ItemId: itemId, SubtitleIndex: subtitleIndex })
        }).then(function (r) {
            if (!r.ok) throw new Error('Sync request failed: ' + r.status);
            return r.json();
        });
    }

    function pollJobStatus(jobId, onProgress, onComplete, onError) {
        var pollInterval = setInterval(function () {
            fetch(SYNC_BASE + '/Jobs/' + jobId, {
                headers: { 'X-Emby-Token': getAccessToken() }
            }).then(function (r) { return r.json(); }).then(function (job) {
                if (job.Status === 'Completed') {
                    clearInterval(pollInterval);
                    onComplete(job);
                } else if (job.Status === 'Failed') {
                    clearInterval(pollInterval);
                    onError(job.Error || 'Sync failed');
                } else {
                    onProgress(job.Progress || 0, job.Phase || null);
                }
            }).catch(function (err) {
                clearInterval(pollInterval);
                onError(err.message);
            });
        }, 2000);
    }

    function formatTime(totalSeconds) {
        var m = Math.floor(totalSeconds / 60);
        var s = totalSeconds % 60;
        return m + ':' + (s < 10 ? '0' : '') + s;
    }

    function showSyncDialog(itemId) {
        var overlay = document.createElement('div');
        overlay.id = 'subsync-dialog';
        overlay.style.cssText = 'position:fixed;top:0;left:0;width:100%;height:100%;background:rgba(0,0,0,0.7);z-index:99999;display:flex;align-items:center;justify-content:center;';

        var dialog = document.createElement('div');
        dialog.style.cssText = 'background:#222;color:#eee;border-radius:12px;padding:24px;max-width:500px;width:90%;max-height:80vh;overflow-y:auto;';

        dialog.innerHTML = '<h2 style="margin:0 0 16px">Sync Subtitles</h2>' +
            '<p id="subsync-status">Loading subtitles...</p>' +
            '<div id="subsync-list"></div>' +
            '<div style="margin-top:16px;text-align:right">' +
            '<button id="subsync-close" style="padding:8px 20px;border:none;border-radius:4px;background:#555;color:#fff;cursor:pointer;margin-right:8px">Close</button>' +
            '</div>';

        overlay.appendChild(dialog);
        document.body.appendChild(overlay);

        overlay.addEventListener('click', function (e) {
            if (e.target === overlay) overlay.remove();
        });
        dialog.querySelector('#subsync-close').addEventListener('click', function () {
            overlay.remove();
        });

        fetchSubtitles(itemId).then(function (subtitles) {
            if (!subtitles || subtitles.length === 0) {
                dialog.querySelector('#subsync-status').textContent = 'No subtitles found for this video.';
                return;
            }

            dialog.querySelector('#subsync-status').textContent = 'Select a subtitle to synchronize:';

            var list = dialog.querySelector('#subsync-list');
            list.innerHTML = '';

            subtitles.forEach(function (sub) {
                var row = document.createElement('div');
                row.style.cssText = 'display:flex;align-items:center;justify-content:space-between;padding:8px 12px;margin:4px 0;background:#333;border-radius:6px;';

                var label = document.createElement('span');
                label.textContent = sub.Title + (sub.IsExternal ? ' (external)' : ' (embedded)');
                if (sub.HasSyncedVersion) {
                    label.textContent += ' \u2713 synced';
                    label.style.color = '#8f8';
                }

                var btn = document.createElement('button');
                btn.textContent = 'Sync';
                btn.style.cssText = 'padding:6px 16px;border:none;border-radius:4px;background:#48c;color:#fff;cursor:pointer;';
                btn.addEventListener('click', function () {
                    btn.disabled = true;
                    btn.textContent = 'Starting...';

                    var startTime = Date.now();

                    startSync(itemId, sub.Index).then(function (job) {
                        btn.textContent = 'Syncing...';
                        row.style.background = '#335';

                        // Add progress detail under the button row
                        var detailRow = document.createElement('div');
                        detailRow.style.cssText = 'padding:4px 12px 8px;font-size:12px;color:#aaa;display:flex;align-items:center;gap:8px;';
                        detailRow.innerHTML = '<span class="subsync-spinner" style="display:inline-block;width:14px;height:14px;border:2px solid #555;border-top-color:#48c;border-radius:50%;animation:subsync-spin 0.8s linear infinite"></span>' +
                            '<span class="subsync-phase">Preparing...</span>' +
                            '<span class="subsync-time" style="margin-left:auto"></span>';
                        row.parentNode.insertBefore(detailRow, row.nextSibling);

                        // Inject spinner keyframes once
                        if (!document.getElementById('subsync-spin-style')) {
                            var style = document.createElement('style');
                            style.id = 'subsync-spin-style';
                            style.textContent = '@keyframes subsync-spin { to { transform: rotate(360deg) } }';
                            document.head.appendChild(style);
                        }

                        pollJobStatus(job.Id,
                            function (progress, phase) {
                                var pct = Math.round(progress * 100);
                                btn.textContent = pct + '%';
                                var phaseEl = detailRow.querySelector('.subsync-phase');
                                if (phaseEl && phase) phaseEl.textContent = phase;
                                // Update elapsed time
                                var elapsed = Math.round((Date.now() - startTime) / 1000);
                                var timeEl = detailRow.querySelector('.subsync-time');
                                if (timeEl) timeEl.textContent = formatTime(elapsed);
                            },
                            function () {
                                btn.textContent = 'Done!';
                                btn.style.background = '#4a4';
                                row.style.background = '#243';
                                label.textContent = label.textContent.replace(' \u2713 synced', '') + ' \u2713 synced';
                                label.style.color = '#8f8';
                                // Remove detail row
                                if (detailRow.parentNode) detailRow.remove();
                            },
                            function (error) {
                                btn.textContent = 'Failed';
                                btn.style.background = '#a44';
                                row.style.background = '#433';
                                // Show error in detail row
                                var spinnerEl = detailRow.querySelector('.subsync-spinner');
                                if (spinnerEl) spinnerEl.style.display = 'none';
                                var phaseEl = detailRow.querySelector('.subsync-phase');
                                if (phaseEl) {
                                    phaseEl.textContent = 'Error: ' + error;
                                    phaseEl.style.color = '#f66';
                                }
                            }
                        );
                    }).catch(function (err) {
                        btn.textContent = 'Error';
                        btn.style.background = '#a44';
                        alert('Failed to start sync: ' + err.message);
                    });
                });

                row.appendChild(label);
                row.appendChild(btn);
                list.appendChild(row);
            });
        }).catch(function (err) {
            dialog.querySelector('#subsync-status').textContent = 'Error: ' + err.message;
        });
    }

    /**
     * Watch for the item context menu (action sheet) appearing and inject the
     * "Sync Subtitles" item into it. A MutationObserver is used instead of
     * hooking the More button's click: the button markup differs across
     * jellyfin-web versions, but the sheet element is stable.
     *
     * The item id comes from the card/row that was actually clicked (works on
     * home rows, library rows and episode lists inside a series page), falling
     * back to the detail-page id from the URL hash. The item type is checked
     * first: containers (series, seasons, folders) never get the menu item,
     * which is what caused the "Failed to fetch subtitles: 404" error.
     */
    function hookActionSheets() {
        var injectPending = false;

        function maybeInject() {
            if (injectPending) return;
            if (document.querySelector('.actionSheetContent [data-id="subsync"]')) return;

            var sheet = document.querySelector('.actionSheetContent');
            if (!sheet) return;

            // Prefer the just-clicked card/row item; fall back to the detail page id.
            var cardFresh = lastMenuCardId && (Date.now() - lastMenuCardTime < 3000);
            var itemId = cardFresh ? lastMenuCardId : getItemIdFromHash();
            if (!itemId) return;
            lastMenuCardId = null;
            lastMenuCardTime = 0;

            injectPending = true;
            canSyncItem(itemId).then(function (canSync) {
                injectPending = false;
                if (canSync === false) return;          // series/season/folder — no menu item
                if (canSync === null && !getItemIdFromHash()) return; // unknown + not a detail page — stay quiet

                var scroller = sheet.querySelector('.actionSheetScroller') || sheet;

                // Create the menu item matching Jellyfin's actionSheet style
                var menuItem = document.createElement('button');
                menuItem.setAttribute('is', 'emby-button');
                menuItem.setAttribute('type', 'button');
                menuItem.setAttribute('data-id', 'subsync');
                menuItem.className = 'listItem listItem-button actionSheetMenuItem emby-button';

                menuItem.innerHTML =
                    '<span class="actionsheetMenuItemIcon listItemIcon listItemIcon-transparent material-icons subtitles" aria-hidden="true"></span>' +
                    '<div class="listItemBody actionsheetListItemBody">' +
                    '<div class="listItemBodyText actionSheetItemText">Sync Subtitles</div>' +
                    '</div>';

                menuItem.addEventListener('click', function () {
                    showSyncDialog(itemId);
                });

                // Insert after "Edit subtitles" if it exists, otherwise append
                var editSubs = sheet.querySelector('[data-id="editsubtitles"]');
                if (editSubs && editSubs.nextSibling && scroller.contains(editSubs)) {
                    scroller.insertBefore(menuItem, editSubs.nextSibling);
                } else {
                    scroller.appendChild(menuItem);
                }

                log('Injected Sync Subtitles menu item (item ' + itemId + ')');
            });
        }

        var observer = new MutationObserver(function () {
            // Only act when a context menu is actually open.
            if (!document.querySelector('.actionSheetContent')) return;
            maybeInject();
        });

        observer.observe(document.documentElement, { childList: true, subtree: true });
    }

    hookActionSheets();
    log('Client script loaded');
})();
