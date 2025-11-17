const API_BASE = "/api";
let autoRefreshInterval = null;
let scheduleUpdateInterval = null;

// Интервалы обновления (в миллисекундах)
const INTERVALS = {
  STATUS: 5000, // /api/status каждые 5 сек
  VERSIONS: 60000, // /api/versions каждые 60 сек (или по событию)
};

// Хранилище таймеров
let timers = {
  status: null,
  versions: null,
};

document.addEventListener("DOMContentLoaded", () => {
  // Начальная загрузка
  loadDashboard();
  loadVersions();

  // Запускаем интервальные обновления
  startPeriodicUpdates();

  // Инициализируем форму расписания
  document
    .getElementById("schedule-form")
    .addEventListener("submit", saveSchedule);
});

/**
 * Logs Management
 */

// Debounce для поиска
let searchTimeout = null;
function debounceLoadLogs() {
  if (searchTimeout) clearTimeout(searchTimeout);
  searchTimeout = setTimeout(loadLogs, 500);
}

function clearLogFilters() {
  document.getElementById("log-level").value = "";
  document.getElementById("log-search").value = "";
  document.getElementById("log-limit").value = "100";
  loadLogs();
}

function toggleAutoRefresh() {
  const autoRefresh = document.getElementById("auto-refresh").checked;

  if (autoRefreshInterval) {
    clearInterval(autoRefreshInterval);
    autoRefreshInterval = null;
  }

  if (autoRefresh) {
    autoRefreshInterval = setInterval(loadLogs, 10000); // 10 seconds
    console.log("Auto-refresh enabled");
  } else {
    console.log("Auto-refresh disabled");
  }
}

async function loadLogs() {
  const level = document.getElementById("log-level").value;
  const search = document.getElementById("log-search").value;
  const limit = document.getElementById("log-limit").value;

  const params = new URLSearchParams();
  if (level) params.append("level", level);
  if (search) params.append("search", search);
  if (limit) params.append("take", limit);

  try {
    const response = await fetch(`${API_BASE}/logs?${params}`);
    if (!response.ok) throw new Error(`HTTP ${response.status}`);

    const data = await response.json();
    displayLogs(data.logs);
    await loadLogStats();
  } catch (error) {
    console.error("Error loading logs:", error);
    document.getElementById(
      "logs-content"
    ).innerHTML = `<div class="log-entry error">Error loading logs: ${error.message}</div>`;
  }
}

async function loadLogStats() {
  try {
    const response = await fetch(`${API_BASE}/logs/stats`);
    if (!response.ok) throw new Error(`HTTP ${response.status}`);

    const stats = await response.json();
    displayLogStats(stats);
  } catch (error) {
    console.error("Error loading log stats:", error);
  }
}

function displayLogStats(stats) {
  const statsDiv = document.getElementById("logs-stats");

  statsDiv.innerHTML = `
        <div class="stats-grid">
            <div class="stat-item">
                <span class="stat-number">${stats.totalEntries}</span>
                <span class="stat-label">Total Entries</span>
            </div>
            <div class="stat-item info">
                <span class="stat-number">${stats.infoCount}</span>
                <span class="stat-label">Info</span>
            </div>
            <div class="stat-item warning">
                <span class="stat-number">${stats.warningCount}</span>
                <span class="stat-label">Warnings</span>
            </div>
            <div class="stat-item error">
                <span class="stat-number">${stats.errorCount}</span>
                <span class="stat-label">Errors</span>
            </div>
            <div class="stat-item">
                <span class="stat-label">Time Range:</span>
                <span class="stat-value">${formatDate(
                  stats.oldestEntry
                )} - ${formatDate(stats.newestEntry)}</span>
            </div>
        </div>
    `;
}

function displayLogs(logs) {
  const container = document.getElementById("logs-content");

  if (!logs || logs.length === 0) {
    container.innerHTML = '<div class="log-entry">No logs found</div>';
    return;
  }

  container.innerHTML = logs
    .map(
      (log) => `
        <div class="log-entry ${log.level.toLowerCase()}">
            <span class="log-col timestamp">${formatDateTime(
              log.timestamp
            )}</span>
            <span class="log-col level">
                <span class="level-badge ${log.level.toLowerCase()}">${
        log.level
      }</span>
            </span>
            <span class="log-col source" title="${log.source}">${truncateText(
        log.source,
        30
      )}</span>
            <span class="log-col message" title="${escapeHtml(log.message)}">
                ${escapeHtml(truncateText(log.message, 100))}
                ${
                  log.exception
                    ? '<span class="exception-indicator" title="Contains exception">⚠️</span>'
                    : ""
                }
            </span>
        </div>
    `
    )
    .join("");
}

async function downloadLogs() {
  try {
    const response = await fetch(`${API_BASE}/logs/download`);
    if (!response.ok) throw new Error(`HTTP ${response.status}`);

    const blob = await response.blob();
    const url = window.URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url;
    a.download = `logs-${new Date()
      .toISOString()
      .slice(0, 19)
      .replace(/:/g, "-")}.zip`;
    document.body.appendChild(a);
    a.click();
    window.URL.revokeObjectURL(url);
    document.body.removeChild(a);

    showToast("Logs downloaded successfully", "success");
  } catch (error) {
    console.error("Error downloading logs:", error);
    showToast(`Error downloading logs: ${error.message}`, "error");
  }
}

/**
 * Schedule Management
 */

async function loadSchedule() {
  try {
    // Проверяем, существует ли элемент формы
    const scheduleForm = document.getElementById("schedule-form");
    if (!scheduleForm) {
      console.warn("Schedule form not found");
      return;
    }

    const [configResponse, statusResponse] = await Promise.all([
      fetch(`${API_BASE}/schedule`),
      fetch(`${API_BASE}/schedule/status`),
    ]);

    if (!configResponse.ok || !statusResponse.ok) {
      throw new Error("Failed to load schedule data");
    }

    const config = await configResponse.json();
    const status = await statusResponse.json();

    displaySchedule(config, status);
  } catch (error) {
    console.error("Error loading schedule:", error);
    showToast(`Error loading schedule: ${error.message}`, "error");
  }
}

function displaySchedule(config, status) {
  // Update status card
  document.getElementById("schedule-status-badge").textContent = status.status;
  document.getElementById(
    "schedule-status-badge"
  ).className = `status-badge ${status.status.toLowerCase()}`;

  document.getElementById("next-check-time").textContent =
    status.nextScheduledCheck
      ? formatDateTime(status.nextScheduledCheck)
      : "Never";

  document.getElementById("time-until-check").textContent =
    status.timeUntilNextCheck ? formatTimeSpan(status.timeUntilNextCheck) : "-";

  document.getElementById("paused-until").textContent = status.config
    .pausedUntil
    ? formatDateTime(status.config.pausedUntil)
    : "Not paused";

  // Update form
  document.getElementById("schedule-enabled").checked = config.enabled;
  document.getElementById("check-time").value = config.checkTime.substring(
    0,
    5
  ); // HH:mm format
  document.getElementById("check-interval").value = config.intervalMinutes;
  document.getElementById("notify-completion").checked =
    config.notifyOnCompletion;
  document.getElementById("notify-errors").checked = config.notifyOnError;

  // Update days checkboxes
  const dayCheckboxes = document.querySelectorAll('input[name="days"]');
  dayCheckboxes.forEach((checkbox) => {
    checkbox.checked = config.daysOfWeek.includes(checkbox.value);
  });
}

async function saveSchedule(event) {
  event.preventDefault();

  const formData = new FormData(event.target);
  const selectedDays = Array.from(
    document.querySelectorAll('input[name="days"]:checked')
  ).map((cb) => cb.value);

  const config = {
    enabled: formData.get("enabled") === "on",
    checkTime: `${formData.get("checkTime")}:00`,
    intervalMinutes: parseInt(formData.get("intervalMinutes")),
    daysOfWeek: selectedDays,
    notifyOnCompletion: formData.get("notifyOnCompletion") === "on",
    notifyOnError: formData.get("notifyOnError") === "on",
  };

  try {
    const response = await fetch(`${API_BASE}/schedule`, {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
      },
      body: JSON.stringify(config),
    });

    if (!response.ok) throw new Error(`HTTP ${response.status}`);

    showToast("Schedule saved successfully", "success");
    await loadSchedule(); // Reload to get updated status
  } catch (error) {
    console.error("Error saving schedule:", error);
    showToast(`Error saving schedule: ${error.message}`, "error");
  }
}

async function pauseSchedule() {
  const duration = document.getElementById("pause-duration").value;

  if (!confirm(`Pause updates for ${duration} hour(s)?`)) return;

  try {
    const response = await fetch(
      `${API_BASE}/schedule/pause?hours=${duration}`,
      {
        method: "POST",
      }
    );

    if (!response.ok) throw new Error(`HTTP ${response.status}`);

    showToast(`Updates paused for ${duration} hour(s)`, "warning");
    await loadSchedule(); // Reload to get updated status
  } catch (error) {
    console.error("Error pausing schedule:", error);
    showToast(`Error pausing schedule: ${error.message}`, "error");
  }
}

async function resumeSchedule() {
  try {
    const response = await fetch(`${API_BASE}/schedule/resume`, {
      method: "POST",
    });

    if (!response.ok) throw new Error(`HTTP ${response.status}`);

    showToast("Updates resumed", "success");
    await loadSchedule(); // Reload to get updated status
  } catch (error) {
    console.error("Error resuming schedule:", error);
    showToast(`Error resuming schedule: ${error.message}`, "error");
  }
}

/**
 * Utility Functions
 */

function formatDateTime(dateString) {
  const date = new Date(dateString);
  return date.toLocaleString();
}

function formatDate(dateString) {
  const date = new Date(dateString);
  return date.toLocaleDateString();
}

function formatTimeSpan(milliseconds) {
  const seconds = Math.floor(milliseconds / 1000);
  const hours = Math.floor(seconds / 3600);
  const minutes = Math.floor((seconds % 3600) / 60);

  if (hours > 0) {
    return `${hours}h ${minutes}m`;
  }
  return `${minutes}m`;
}

function truncateText(text, maxLength) {
  if (!text) return "";
  return text.length > maxLength ? text.substring(0, maxLength) + "..." : text;
}

function showToast(message, type = "info") {
  // Simple toast implementation
  const toast = document.createElement("div");
  toast.className = `toast toast-${type}`;
  toast.textContent = message;
  toast.style.cssText = `
        position: fixed;
        top: 20px;
        right: 20px;
        padding: 12px 20px;
        background: ${
          type === "error"
            ? "#f44336"
            : type === "success"
            ? "#4caf50"
            : "#2196f3"
        };
        color: white;
        border-radius: 4px;
        z-index: 1000;
        animation: slideIn 0.3s ease-out;
    `;

  document.body.appendChild(toast);

  setTimeout(() => {
    toast.style.animation = "slideOut 0.3s ease-in";
    setTimeout(() => {
      if (toast.parentNode) {
        toast.parentNode.removeChild(toast);
      }
    }, 300);
  }, 3000);
}

// Add CSS animations
const style = document.createElement("style");
style.textContent = `
    @keyframes slideIn {
        from { transform: translateX(100%); opacity: 0; }
        to { transform: translateX(0); opacity: 1; }
    }

    @keyframes slideOut {
        from { transform: translateX(0); opacity: 1; }
        to { transform: translateX(100%); opacity: 0; }
    }
`;
document.head.appendChild(style);

/**
 * Запускает периодические обновления данных
 */
function startPeriodicUpdates() {
  // /api/status каждые 5 секунд
  if (timers.status) clearInterval(timers.status);
  timers.status = setInterval(() => {
    loadDashboard();
  }, INTERVALS.STATUS);

  // /api/versions каждые 60 секунд
  if (timers.versions) clearInterval(timers.versions);
  timers.versions = setInterval(() => {
    loadVersions();
  }, INTERVALS.VERSIONS);
}

/**
 * Переключение между вкладками changelog
 */
function switchChangelogTab(e, tabName) {
  e.preventDefault();
  document
    .querySelectorAll(".changelog-tab")
    .forEach((t) => t.classList.remove("active"));
  document
    .querySelectorAll(".changelog-container")
    .forEach((c) => (c.style.display = "none"));
  document.getElementById(tabName).classList.add("active");

  // Загружаем данные для выбранной вкладки
  if (tabName === "global-changelog") {
    loadGlobalChangelog();
  } else if (tabName === "history") {
    loadVersionHistory();
  }
}

/**
 * Загружает глобальный changelog
 */
async function loadGlobalChangelog() {
  const contentDiv = document.getElementById("global-changelog-content");
  contentDiv.innerHTML = '<p style="color: #999;">Loading...</p>';

  try {
    const response = await fetch(`${API_BASE}/changelog`);

    if (!response.ok) {
      if (response.status === 404) {
        contentDiv.innerHTML =
          '<p style="color: #999;">Global changelog not available yet. Downloads will be started soon.</p>';
        return;
      }
      throw new Error(`HTTP ${response.status}`);
    }

    const text = await response.text();
    contentDiv.innerHTML = `<pre>${escapeHtml(text)}</pre>`;
  } catch (e) {
    console.error("Error loading global changelog:", e);
    contentDiv.innerHTML = `<p style="color: #d32f2f;">Error loading changelog: ${e.message}</p>`;
  }
}

/**
 * Загружает changelog для конкретной версии
 */
async function loadVersionChangelog() {
  const select = document.getElementById("version-select");
  const version = select.value;

  if (!version) {
    alert("Please select a version");
    return;
  }

  const contentDiv = document.getElementById("version-changelog-content");
  contentDiv.innerHTML = '<p style="color: #999;">Loading...</p>';

  try {
    const response = await fetch(`${API_BASE}/changelog/${version}`);

    if (!response.ok) {
      if (response.status === 404) {
        contentDiv.innerHTML = `<p style="color: #999;">Changelog not available for version ${version}</p>`;
        return;
      }
      throw new Error(`HTTP ${response.status}`);
    }

    const text = await response.text();
    contentDiv.innerHTML = `<pre>${escapeHtml(text)}</pre>`;
  } catch (e) {
    console.error("Error loading version changelog:", e);
    contentDiv.innerHTML = `<p style="color: #d32f2f;">Error loading changelog: ${e.message}</p>`;
  }
}

/**
 * Загружает историю версий
 */
async function loadVersionHistory() {
  const contentDiv = document.getElementById("history-list");
  contentDiv.innerHTML =
    '<tr><td colspan="4" style="text-align: center; color: #999;">Loading...</td></tr>';

  try {
    const response = await fetch(`${API_BASE}/versions/history?take=50`);

    if (!response.ok) {
      throw new Error(`HTTP ${response.status}`);
    }

    const data = await response.json();

    if (!data.data || data.data.length === 0) {
      contentDiv.innerHTML =
        '<tr><td colspan="4" style="text-align: center; color: #999;">No history available</td></tr>';
      return;
    }

    contentDiv.innerHTML = data.data
      .map(
        (log) => `
            <tr>
                <td>${new Date(log.timestamp).toLocaleString()}</td>
                <td><strong>${log.v6Stable || "-"}</strong></td>
                <td>${log.v7Fixed || "-"}</td>
                <td>${log.v7Stable || "-"}</td>
            </tr>
        `
      )
      .join("");
  } catch (e) {
    console.error("Error loading version history:", e);
    contentDiv.innerHTML = `<tr><td colspan="4" style="text-align: center; color: #d32f2f;">Error: ${e.message}</td></tr>`;
  }
}

/**
 * Обновляет выпадающий список версий при загрузке вкладки versions
 */
function populateVersionSelect() {
  const select = document.getElementById("version-select");
  if (!select) return;

  const allVersions = [];

  // Собираем все версии v6
  const v6ListRows = document.querySelectorAll("#v6-list tr");
  v6ListRows.forEach((row) => {
    const versionCell = row.querySelector("td:first-child strong");
    if (versionCell) {
      allVersions.push(versionCell.textContent);
    }
  });

  // Собираем все версии v7
  const v7ListRows = document.querySelectorAll("#v7-list tr");
  v7ListRows.forEach((row) => {
    const versionCell = row.querySelector("td:first-child strong");
    if (versionCell) {
      allVersions.push(versionCell.textContent);
    }
  });

  // Заполняем select
  const currentValue = select.value;
  select.innerHTML = '<option value="">Select a version...</option>';

  allVersions.forEach((version) => {
    const option = document.createElement("option");
    option.value = version;
    option.textContent = version;
    select.appendChild(option);
  });

  // Восстанавливаем значение если оно было
  if (currentValue && allVersions.includes(currentValue)) {
    select.value = currentValue;
  }
}

/**
 * Вспомогательная функция для экранирования HTML
 */
function escapeHtml(text) {
  const map = {
    "&": "&amp;",
    "<": "&lt;",
    ">": "&gt;",
    '"': "&quot;",
    "'": "&#039;",
  };
  return text.replace(/[&<>"']/g, (m) => map[m]);
}

// Обновляем loadVersions() чтобы также обновлять выпадающий список
const originalLoadVersions = loadVersions;
loadVersions = async function () {
  await originalLoadVersions();
  populateVersionSelect();
};

/**
 * Останавливает периодические обновления (для cleanup)
 */
function stopPeriodicUpdates() {
  if (timers.status) clearInterval(timers.status);
  if (timers.versions) clearInterval(timers.versions);
}

function switchTab(e, tabName) {
  e.preventDefault();
  document
    .querySelectorAll(".tab-pane")
    .forEach((t) => t.classList.remove("active"));
  document
    .querySelectorAll(".nav-link")
    .forEach((l) => l.classList.remove("active"));
  document.getElementById(tabName).classList.add("active");
  e.target.classList.add("active");

  // Загружаем данные для активной вкладки
  if (tabName === "logs") {
    loadLogs();
  } else if (tabName === "schedule") {
    loadSchedule();
    // Запускаем обновление статуса каждую минуту
    if (scheduleUpdateInterval) clearInterval(scheduleUpdateInterval);
    scheduleUpdateInterval = setInterval(loadSchedule, 60000);
  } else {
    // Останавливаем обновление статуса расписания если ушли с вкладки
    if (scheduleUpdateInterval) {
      clearInterval(scheduleUpdateInterval);
      scheduleUpdateInterval = null;
    }

    if (tabName === "config") {
      loadAllowedArches();
    }
  }
}

async function loadAllowedArches() {
  const container = document.getElementById("arches-container");
  if (!container) return;

  try {
    const response = await fetch(`${API_BASE}/settings/arches`);
    if (!response.ok) {
      // Если эндпоинта нет – тихо выходим, чтобы UI не ломался
      console.warn("Failed to load allowed arches:", response.status);
      return;
    }

    const arches = await response.json();
    const set = new Set(arches.map((a) => a.toLowerCase()));

    container.querySelectorAll('input[type="checkbox"]').forEach((cb) => {
      cb.checked = set.has(cb.value.toLowerCase());
    });

    const status = document.getElementById("arches-status");
    if (status) {
      status.textContent =
        arches.length > 0
          ? `Loaded: ${arches.join(", ")}`
          : "Loaded default architectures";
    }
  } catch (e) {
    console.error("Error loading allowed arches:", e);
    showToast(`Error loading architectures: ${e.message}`, "error");
  }
}

async function saveAllowedArches() {
  const container = document.getElementById("arches-container");
  if (!container) return;

  const selected = Array.from(
    container.querySelectorAll('input[type="checkbox"]:checked')
  ).map((cb) => cb.value);

  // Можно предупредить, если вообще всё сняли
  if (selected.length === 0) {
    if (
      !confirm(
        "No architectures selected. Default list will be used on the server. Continue?"
      )
    ) {
      return;
    }
  }

  try {
    const response = await fetch(`${API_BASE}/settings/arches`, {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
      },
      body: JSON.stringify(selected),
    });

    if (!response.ok) throw new Error(`HTTP ${response.status}`);

    showToast("Allowed architectures saved", "success");

    const status = document.getElementById("arches-status");
    if (status) {
      status.textContent =
        selected.length > 0
          ? `Saved: ${selected.join(", ")}`
          : "Saved: default architectures will be used";
    }
  } catch (e) {
    console.error("Error saving allowed arches:", e);
    showToast(`Error saving architectures: ${e.message}`, "error");
  }
}

function switchVersionTab(e, tabName) {
  e.preventDefault();
  document
    .querySelectorAll(".version-tab")
    .forEach((t) => t.classList.remove("active"));
  document
    .querySelectorAll(".tab-btn")
    .forEach((b) => b.classList.remove("active"));
  document.getElementById(tabName).classList.add("active");
  e.target.classList.add("active");
}

async function loadDashboard() {
  try {
    const response = await fetch(`${API_BASE}/status`);
    const data = await response.json();

    document.getElementById("server-status").textContent = "🟢 Online";
    document.getElementById("last-check").textContent = data.lastCheck
      ? new Date(data.lastCheck).toLocaleString()
      : "Pending...";
    document.getElementById(
      "uptime"
    ).textContent = `${data.uptime.days}d ${data.uptime.hours}h ${data.uptime.minutes}m`;
    document.getElementById("memory").textContent = data.process.memory;

    // Исправленное отображение потоков
    if (typeof data.process.threads === "object") {
      document.getElementById(
        "threads"
      ).textContent = `${data.process.threads.threadPoolActive}/${data.process.threads.maxWorkerThreads}`;
    } else {
      document.getElementById("threads").textContent = data.process.threads;
    }

    document.getElementById("cpuUsage").textContent = data.process.cpuUsage;
    const diskElem = document.getElementById("disk");
    if (diskElem) {
      diskElem.textContent = data.diskUsage
        ? `${data.diskUsage.totalGB} GB`
        : "-";
    }
    document.getElementById("total-files").textContent = data.downloads.files;
    document.getElementById("total-gb").textContent = data.downloads.total;
  } catch (e) {
    console.error("Failed to load dashboard:", e);
    document.getElementById("server-status").textContent = "🔴 Offline";
  }
}

async function loadVersions() {
  try {
    const response = await fetch(`${API_BASE}/versions`);
    const data = await response.json();

    document.getElementById("v6-active").textContent = data.v6.active || "-";
    document.getElementById("v7-fixed").textContent =
      data.v7.activeFixed || "-";
    document.getElementById("v7-latest").textContent =
      data.v7.activeLatest || "-";

    updateTable("v6", data.v6.versions, data.v6.active);
    updateTable(
      "v7",
      data.v7.versions,
      data.v7.activeFixed,
      data.v7.activeLatest
    );
  } catch (e) {
    console.error("Error loading versions:", e);
  }
}

function updateTable(branch, versions, ...active) {
  const tbody = document.getElementById(`${branch}-list`);
  tbody.innerHTML = "";

  versions.forEach((v) => {
    const isActive = active.includes(v);
    const row = document.createElement("tr");

    if (branch === "v6") {
      row.innerHTML = `<td><strong>${v}</strong></td><td><span class="status-badge ${
        isActive ? "active" : "inactive"
      }">${
        isActive ? "✓" : "✗"
      }</span></td><td><button class="btn-set" onclick="setVersion('${v}')">Set</button>${
        !isActive
          ? `<button class="btn-delete" onclick="removeVersion('${v}')">Delete</button>`
          : ""
      }</td>`;
    } else {
      const isFixed = v === active[0]; // activeFixed первый, activeLatest второй
      const isLatest = v === active[1];
      const type = isFixed ? "Fixed" : isLatest ? "Latest" : "";

      row.innerHTML = `<td><strong>${v}</strong></td><td>${type}</td><td><span class="status-badge ${
        isActive ? "active" : "inactive"
      }">${
        isActive ? "✓" : "✗"
      }</span></td><td><button class="btn-set" onclick="setVersion('${v}')">Set</button>${
        !isActive
          ? `<button class="btn-delete" onclick="removeVersion('${v}')">Delete</button>`
          : ""
      }</td>`;
    }

    tbody.appendChild(row);
  });
}

async function setVersion(v) {
  try {
    const response = await fetch(`${API_BASE}/set-active-version/${v}`, {
      method: "POST",
    });

    if (!response.ok) {
      const error = await response.json();
      alert(`❌ Error: ${error.message || error.code}`);
      console.error("Set version error:", error);
      return;
    }

    await loadVersions();
    console.log(`✓ Version ${v} set as active`);
  } catch (e) {
    console.error("Set version error:", e);
    alert(`❌ Error: ${e.message}`);
  }
}

async function removeVersion(v) {
  if (!confirm(`Delete version ${v}?`)) return;

  try {
    const response = await fetch(`${API_BASE}/remove-version/${v}`, {
      method: "DELETE",
    });

    if (!response.ok) {
      const error = await response.json();
      alert(`❌ Error: ${error.message || error.code}`);
      console.error("Remove version error:", error);
      return;
    }

    await loadVersions();
    console.log(`✓ Version ${v} removed`);
  } catch (e) {
    console.error("Remove version error:", e);
    alert(`❌ Error: ${e.message}`);
  }
}

async function checkUpdates(e) {
  const btn = e.target;
  btn.disabled = true;
  btn.textContent = "⟳ Checking...";

  try {
    const response = await fetch(`${API_BASE}/update-check`, {
      method: "POST",
    });

    if (response.status === 409) {
      const error = await response.json();
      alert(`⚠️ ${error.message}`);
      return;
    }

    if (response.status === 503) {
      const error = await response.json();
      alert(
        `❌ Service Unavailable:\n\n${error.message}\n\n${error.details || ""}`
      );
      return;
    }

    if (response.status === 504) {
      const error = await response.json();
      alert(`⏱️ Timeout:\n\n${error.message}\n\n${error.details || ""}`);
      return;
    }

    if (!response.ok) {
      const error = await response.json();
      alert(`❌ Error: ${error.message || "Failed to check updates"}`);
      return;
    }

    // Успешная проверка
    const result = await response.json();
    alert(
      `✓ Update check completed!\n\nDownloaded: ${result.downloaded} files\nVersions checked: ${result.checkedVersions.length}`
    );

    // Обновляем данные сразу
    await new Promise((r) => setTimeout(r, 1000));
    await loadDashboard();
    await loadVersions();
  } catch (e) {
    console.error("Network error:", e);
    alert(
      `❌ Network Error:\n\n${e.message}\n\nCheck browser console for details.`
    );
  } finally {
    btn.disabled = false;
    btn.textContent = "⟳ Check Updates";
  }
}
