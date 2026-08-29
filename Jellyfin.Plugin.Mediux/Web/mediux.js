const MediuxConfig = {
    pluginUniqueId: 'c8e4f2a1-9b3d-4e6f-a1c2-7d8e9f0a1b2c'
};

export default function (view) {
    wirePriorityList(view);
    wireAuthorList(view, '#authorExcludedList', '#btnAddExcludedAuthor');

    view.querySelector('#chkEnableUpgradeUntil').addEventListener('change', function () {
        syncUpgradeUntilRow(view.querySelector('#authorPriorityList'), this.checked);
    });

    view.addEventListener('viewshow', function () {
        Dashboard.showLoadingMsg();
        var page = this;
        ApiClient.getPluginConfiguration(MediuxConfig.pluginUniqueId).then(function (config) {
            page.querySelector('#txtApiKey').value = config.ApiKey || '';
            page.querySelector('#selectDownloadQuality').value = config.DownloadQuality || 'optimized';
            page.querySelector('#chkMapAlbumArtToBox').checked = !!config.MapAlbumArtToBox;
            page.querySelector('#chkOnlyPrioritizedAuthors').checked = !!config.OnlyPrioritizedAuthors;
            page.querySelector('#chkEnableUpgradeUntil').checked = !!config.EnableUpgradeUntil;
            page.querySelector('#txtSetDownloadConcurrency').value = clampConcurrency(config.SetDownloadConcurrency);
            page.querySelector('#txtSetListCacheDays').value = clampCacheDays(config.SetListCacheDays);
            page.querySelector('#chkRememberBrowseBy').checked = !!config.RememberBrowseBy;
            renderPriorityList(
                page.querySelector('#authorPriorityList'),
                config.PriorityCreators || '',
                !!config.EnableUpgradeUntil,
                config.UpgradeUntilIndex);
            renderAuthorList(page.querySelector('#authorExcludedList'), config.ExcludedCreators || '');
            Dashboard.hideLoadingMsg();
        });
    });

    view.querySelector('#MediuxConfigForm').addEventListener('submit', function (e) {
        e.preventDefault();
        saveConfig(view);
        return false;
    });

    view.querySelector('#btnSave').addEventListener('click', function (e) {
        e.preventDefault();
        saveConfig(view);
    });
}

function wirePriorityList(view) {
    var list = view.querySelector('#authorPriorityList');
    var dragRow = null;

    view.querySelector('#btnAddAuthor').addEventListener('click', function () {
        var upgradeRow = list.querySelector('.mediux-upgrade-until-row');
        var row = createAuthorRow('');
        if (upgradeRow) {
            list.insertBefore(row, upgradeRow);
        } else {
            list.appendChild(row);
        }
        focusLastAuthorInput(list);
        clampUpgradeUntilPosition(list);
    });

    list.addEventListener('click', function (e) {
        var target = e.target.closest('button');
        if (!target) {
            return;
        }

        var row = target.closest('.mediux-author-row, .mediux-upgrade-until-row');
        if (!row) {
            return;
        }

        if (target.classList.contains('btnAuthorUp')) {
            movePriorityRow(list, row, -1);
            return;
        }

        if (target.classList.contains('btnAuthorDown')) {
            movePriorityRow(list, row, 1);
            return;
        }

        if (target.classList.contains('btnAuthorRemove') && row.classList.contains('mediux-author-row')) {
            row.remove();
            if (!list.querySelector('.mediux-author-row')) {
                var upgradeRow = list.querySelector('.mediux-upgrade-until-row');
                var empty = createAuthorRow('');
                if (upgradeRow) {
                    list.insertBefore(empty, upgradeRow);
                } else {
                    list.appendChild(empty);
                }
            }
            clampUpgradeUntilPosition(list);
        }
    });

    list.addEventListener('dragstart', function (e) {
        var row = e.target.closest('.mediux-author-row, .mediux-upgrade-until-row');
        if (!row) {
            return;
        }

        dragRow = row;
        row.classList.add('mediux-dragging');
        if (e.dataTransfer) {
            e.dataTransfer.effectAllowed = 'move';
            e.dataTransfer.setData('text/plain', '');
        }
    });

    list.addEventListener('dragend', function () {
        if (dragRow) {
            dragRow.classList.remove('mediux-dragging');
        }
        dragRow = null;
        clampUpgradeUntilPosition(list);
    });

    list.addEventListener('dragover', function (e) {
        e.preventDefault();
        if (!dragRow) {
            return;
        }

        var over = e.target.closest('.mediux-author-row, .mediux-upgrade-until-row');
        if (!over || over === dragRow) {
            return;
        }

        var rect = over.getBoundingClientRect();
        var before = (e.clientY - rect.top) < rect.height / 2;
        list.insertBefore(dragRow, before ? over : over.nextElementSibling);
    });

    list.addEventListener('drop', function (e) {
        e.preventDefault();
        clampUpgradeUntilPosition(list);
    });
}

function movePriorityRow(list, row, direction) {
    if (direction < 0) {
        var prev = row.previousElementSibling;
        if (!prev) {
            return;
        }
        if (row.classList.contains('mediux-upgrade-until-row') && !prev.previousElementSibling) {
            // Cannot move upgrade-until above the first author.
            return;
        }
        list.insertBefore(row, prev);
    } else {
        var next = row.nextElementSibling;
        if (!next) {
            return;
        }
        list.insertBefore(next, row);
    }
    clampUpgradeUntilPosition(list);
}

function clampUpgradeUntilPosition(list) {
    var upgradeRow = list.querySelector('.mediux-upgrade-until-row');
    if (!upgradeRow) {
        return;
    }

    var firstAuthor = list.querySelector('.mediux-author-row');
    if (!firstAuthor) {
        return;
    }

    // Upgrade-until must not be the first row.
    if (upgradeRow === list.firstElementChild || upgradeRow.compareDocumentPosition(firstAuthor) & Node.DOCUMENT_POSITION_FOLLOWING) {
        list.insertBefore(firstAuthor, upgradeRow);
    }
}

function syncUpgradeUntilRow(list, enabled) {
    var existing = list.querySelector('.mediux-upgrade-until-row');
    if (!enabled) {
        if (existing) {
            existing.remove();
        }
        return;
    }

    if (!existing) {
        var authors = list.querySelectorAll('.mediux-author-row');
        var row = createUpgradeUntilRow();
        if (authors.length) {
            // Default: after first author (index 1).
            if (authors[0].nextElementSibling) {
                list.insertBefore(row, authors[0].nextElementSibling);
            } else {
                list.appendChild(row);
            }
        } else {
            list.appendChild(createAuthorRow(''));
            list.appendChild(row);
        }
    }
    clampUpgradeUntilPosition(list);
}

function wireAuthorList(view, listSelector, addButtonSelector) {
    var list = view.querySelector(listSelector);
    var dragRow = null;

    view.querySelector(addButtonSelector).addEventListener('click', function () {
        list.appendChild(createAuthorRow(''));
        focusLastAuthorInput(list);
    });

    list.addEventListener('click', function (e) {
        var target = e.target.closest('button');
        if (!target) {
            return;
        }

        var row = target.closest('.mediux-author-row');
        if (!row) {
            return;
        }

        if (target.classList.contains('btnAuthorUp')) {
            if (row.previousElementSibling) {
                list.insertBefore(row, row.previousElementSibling);
            }
            return;
        }

        if (target.classList.contains('btnAuthorDown')) {
            if (row.nextElementSibling) {
                list.insertBefore(row.nextElementSibling, row);
            }
            return;
        }

        if (target.classList.contains('btnAuthorRemove')) {
            row.remove();
            if (!list.querySelector('.mediux-author-row')) {
                list.appendChild(createAuthorRow(''));
            }
        }
    });

    list.addEventListener('dragstart', function (e) {
        var row = e.target.closest('.mediux-author-row');
        if (!row) {
            return;
        }

        dragRow = row;
        row.classList.add('mediux-dragging');
        if (e.dataTransfer) {
            e.dataTransfer.effectAllowed = 'move';
            e.dataTransfer.setData('text/plain', '');
        }
    });

    list.addEventListener('dragend', function () {
        if (dragRow) {
            dragRow.classList.remove('mediux-dragging');
        }
        dragRow = null;
    });

    list.addEventListener('dragover', function (e) {
        e.preventDefault();
        if (!dragRow) {
            return;
        }

        var over = e.target.closest('.mediux-author-row');
        if (!over || over === dragRow) {
            return;
        }

        var rect = over.getBoundingClientRect();
        var before = (e.clientY - rect.top) < rect.height / 2;
        list.insertBefore(dragRow, before ? over : over.nextElementSibling);
    });

    list.addEventListener('drop', function (e) {
        e.preventDefault();
    });
}

function createAuthorRow(username) {
    var row = document.createElement('div');
    row.className = 'listItem viewItem section-row mediux-author-row';
    row.draggable = true;

    row.innerHTML = [
        '<span class="material-icons drag_handle" aria-hidden="true"></span>',
        '<div class="listItemBody">',
        '  <input type="text" class="txtAuthorName" placeholder="MediUX Username..." autocomplete="off" spellcheck="false" />',
        '</div>',
        '<button type="button" is="paper-icon-button-light" class="btnAuthorUp btnViewItemMove autoSize paper-icon-button-light" title="Up">',
        '  <span class="material-icons keyboard_arrow_up" aria-hidden="true"></span>',
        '</button>',
        '<button type="button" is="paper-icon-button-light" class="btnAuthorDown btnViewItemMove autoSize paper-icon-button-light" title="Down">',
        '  <span class="material-icons keyboard_arrow_down" aria-hidden="true"></span>',
        '</button>',
        '<button type="button" is="paper-icon-button-light" class="btnAuthorRemove autoSize paper-icon-button-light" title="Remove">',
        '  <span class="material-icons remove" aria-hidden="true"></span>',
        '</button>'
    ].join('');

    row.querySelector('.txtAuthorName').value = username || '';

    row.querySelector('.txtAuthorName').addEventListener('mousedown', function (e) {
        e.stopPropagation();
    });

    var buttons = row.querySelectorAll('button');
    for (var i = 0; i < buttons.length; i++) {
        buttons[i].draggable = false;
        buttons[i].addEventListener('mousedown', function (e) {
            e.stopPropagation();
        });
    }

    return row;
}

function createUpgradeUntilRow() {
    var row = document.createElement('div');
    row.className = 'listItem viewItem section-row mediux-upgrade-until-row';
    row.draggable = true;
    row.setAttribute('data-upgrade-until', '1');

    row.innerHTML = [
        '<span class="material-icons drag_handle" aria-hidden="true"></span>',
        '<div class="listItemBody">',
        '  <span class="material-icons keyboard_arrow_up" aria-hidden="true"></span>',
        '  <span>Upgrade Until</span>',
        '  <span class="material-icons keyboard_arrow_up" aria-hidden="true"></span>',
        '</div>',
        '<button type="button" is="paper-icon-button-light" class="btnAuthorUp btnViewItemMove autoSize paper-icon-button-light" title="Up">',
        '  <span class="material-icons keyboard_arrow_up" aria-hidden="true"></span>',
        '</button>',
        '<button type="button" is="paper-icon-button-light" class="btnAuthorDown btnViewItemMove autoSize paper-icon-button-light" title="Down">',
        '  <span class="material-icons keyboard_arrow_down" aria-hidden="true"></span>',
        '</button>'
    ].join('');

    var buttons = row.querySelectorAll('button');
    for (var i = 0; i < buttons.length; i++) {
        buttons[i].draggable = false;
        buttons[i].addEventListener('mousedown', function (e) {
            e.stopPropagation();
        });
    }

    return row;
}

function renderPriorityList(list, creators, enableUpgradeUntil, upgradeUntilIndex) {
    list.innerHTML = '';

    var authors = String(creators || '')
        .split(/[\r\n,;]+/)
        .map(function (s) { return s.trim(); })
        .filter(function (s) { return !!s; });

    if (authors.length === 0) {
        list.appendChild(createAuthorRow(''));
    } else {
        authors.forEach(function (name) {
            list.appendChild(createAuthorRow(name));
        });
    }

    if (enableUpgradeUntil) {
        var idx = parseInt(upgradeUntilIndex, 10);
        if (!isFinite(idx) || isNaN(idx) || idx < 1) {
            idx = 1;
        }
        if (idx > authors.length) {
            idx = authors.length;
        }

        var row = createUpgradeUntilRow();
        var authorRows = list.querySelectorAll('.mediux-author-row');
        if (idx >= authorRows.length) {
            list.appendChild(row);
        } else {
            list.insertBefore(row, authorRows[idx]);
        }
        clampUpgradeUntilPosition(list);
    }
}

function renderAuthorList(list, creators) {
    list.innerHTML = '';

    var authors = String(creators || '')
        .split(/[\r\n,;]+/)
        .map(function (s) { return s.trim(); })
        .filter(function (s) { return !!s; });

    if (authors.length === 0) {
        list.appendChild(createAuthorRow(''));
        return;
    }

    authors.forEach(function (name) {
        list.appendChild(createAuthorRow(name));
    });
}

function collectCreators(page, listSelector) {
    var inputs = page.querySelectorAll(listSelector + ' .txtAuthorName');
    var names = [];
    var seen = {};

    for (var i = 0; i < inputs.length; i++) {
        var name = (inputs[i].value || '').trim();
        if (!name) {
            continue;
        }

        var key = name.toLowerCase();
        if (seen[key]) {
            continue;
        }

        seen[key] = true;
        names.push(name);
    }

    return names.join('\n');
}

function collectUpgradeUntilIndex(list) {
    var index = 0;
    var child = list.firstElementChild;
    while (child) {
        if (child.classList.contains('mediux-upgrade-until-row')) {
            return Math.max(1, index);
        }
        if (child.classList.contains('mediux-author-row')) {
            var name = (child.querySelector('.txtAuthorName') && child.querySelector('.txtAuthorName').value || '').trim();
            if (name) {
                index++;
            }
        }
        child = child.nextElementSibling;
    }
    return Math.max(1, index);
}

function focusLastAuthorInput(list) {
    var inputs = list.querySelectorAll('.txtAuthorName');
    if (inputs.length) {
        inputs[inputs.length - 1].focus();
    }
}

function clampConcurrency(value) {
    var n = parseInt(value, 10);
    if (!isFinite(n) || isNaN(n)) {
        return 6;
    }
    if (n < 1) {
        return 1;
    }
    if (n > 16) {
        return 16;
    }
    return n;
}

function clampCacheDays(value) {
    var n = parseInt(value, 10);
    if (!isFinite(n) || isNaN(n)) {
        return 1;
    }
    if (n < 0) {
        return 0;
    }
    if (n > 30) {
        return 30;
    }
    return n;
}

function saveConfig(page) {
    Dashboard.showLoadingMsg();
    ApiClient.getPluginConfiguration(MediuxConfig.pluginUniqueId).then(function (config) {
        var priorityList = page.querySelector('#authorPriorityList');
        config.ApiKey = page.querySelector('#txtApiKey').value;
        config.PriorityCreators = collectCreators(page, '#authorPriorityList');
        config.ExcludedCreators = collectCreators(page, '#authorExcludedList');
        config.OnlyPrioritizedAuthors = page.querySelector('#chkOnlyPrioritizedAuthors').checked;
        config.EnableUpgradeUntil = page.querySelector('#chkEnableUpgradeUntil').checked;
        config.UpgradeUntilIndex = collectUpgradeUntilIndex(priorityList);
        config.DownloadQuality = page.querySelector('#selectDownloadQuality').value;
        config.MapAlbumArtToBox = page.querySelector('#chkMapAlbumArtToBox').checked;
        config.SetDownloadConcurrency = clampConcurrency(page.querySelector('#txtSetDownloadConcurrency').value);
        config.SetListCacheDays = clampCacheDays(page.querySelector('#txtSetListCacheDays').value);
        config.RememberBrowseBy = page.querySelector('#chkRememberBrowseBy').checked;
        delete config.IncludeNonPreferredSets;

        ApiClient.updatePluginConfiguration(MediuxConfig.pluginUniqueId, config).then(function (result) {
            Dashboard.processPluginConfigurationUpdateResult(result);
        });
    });
}
