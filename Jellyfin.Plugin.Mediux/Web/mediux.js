const MediuxConfig = {
    pluginUniqueId: 'c8e4f2a1-9b3d-4e6f-a1c2-7d8e9f0a1b2c'
};

export default function (view) {
    wireAuthorList(view, '#authorPriorityList', '#btnAddAuthor');
    wireAuthorList(view, '#authorExcludedList', '#btnAddExcludedAuthor');

    view.addEventListener('viewshow', function () {
        Dashboard.showLoadingMsg();
        var page = this;
        ApiClient.getPluginConfiguration(MediuxConfig.pluginUniqueId).then(function (config) {
            page.querySelector('#txtApiKey').value = config.ApiKey || '';
            page.querySelector('#selectDownloadQuality').value = config.DownloadQuality || 'optimized';
            page.querySelector('#chkMapAlbumArtToBox').checked = !!config.MapAlbumArtToBox;
            page.querySelector('#chkOnlyPrioritizedAuthors').checked = !!config.OnlyPrioritizedAuthors;
            page.querySelector('#txtSetDownloadConcurrency').value = clampConcurrency(config.SetDownloadConcurrency);
            renderAuthorList(page.querySelector('#authorPriorityList'), config.PriorityCreators || '');
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

function saveConfig(page) {
    Dashboard.showLoadingMsg();
    ApiClient.getPluginConfiguration(MediuxConfig.pluginUniqueId).then(function (config) {
        config.ApiKey = page.querySelector('#txtApiKey').value;
        config.PriorityCreators = collectCreators(page, '#authorPriorityList');
        config.ExcludedCreators = collectCreators(page, '#authorExcludedList');
        config.OnlyPrioritizedAuthors = page.querySelector('#chkOnlyPrioritizedAuthors').checked;
        config.DownloadQuality = page.querySelector('#selectDownloadQuality').value;
        config.MapAlbumArtToBox = page.querySelector('#chkMapAlbumArtToBox').checked;
        config.SetDownloadConcurrency = clampConcurrency(page.querySelector('#txtSetDownloadConcurrency').value);
        delete config.IncludeNonPreferredSets;

        ApiClient.updatePluginConfiguration(MediuxConfig.pluginUniqueId, config).then(function (result) {
            Dashboard.processPluginConfigurationUpdateResult(result);
        });
    });
}
