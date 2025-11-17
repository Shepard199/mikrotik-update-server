const API_BASE = "/api";

// Update intervals (milliseconds)
const INTERVALS = {
  STATUS: 5000, // /api/status every 5 sec
  VERSIONS: 60000, // /api/versions every 60 sec
  SCHEDULE: 60000, // Schedule status every 60 sec
};

// Timer storage
const timers = {
  status: null,
  versions: null,
  schedule: null,
};

// Auto-refresh intervals
let autoRefreshInterval = null;

/**
 * Initialize application on DOM ready
 */
document.addEventListener("DOMContentLoaded", () => {
  loadDashboard();
  loadVersions();
  startPeriodicUpdates();

  const scheduleForm = document.getElementById("schedule-form");
  if (scheduleForm) {
    scheduleForm.addEventListener("submit", saveSchedule);
  }
});

/**
 * ============================================================================
 * DASHBOARD & STATUS
 * ============================================================================
 */

async function loadDashboard() {
  try {
    const response = await fetch(`${API_BASE}/status`);
    if (!response.ok) throw new Error(`HTTP ${response.status}`);

    const data = await response.json();
    updateDashboardUI(data);
  } catch (error) {
    console.error("Failed to load dashboard:", error);
    setOfflineStatus();
  }
}

function updateDashboardUI(data) {
  document.getElementById("server-status").textContent = "🟢 Online";
  document.getElementById("last-check").textContent = data.lastCheck
    ? new Date(data.lastCheck).toLocaleString()
    : "Pending...";
  document.getElementById(
    "uptime"
  ).textContent = `${data.uptime.days}d ${data.uptime.hours}h ${data.uptime.minutes}m`;
  document.getElementById("memory").textContent = data.process.memory;
  document.getElementById("cpuUsage").textContent = data.process.cpuUsage;
  document.getElementById("total-files").textContent = data.downloads.files;
  document.getElementById("total-gb").textContent = data.downloads.total;

  // Thread count handling
  if (typeof data.process.threads === "object") {
    document.getElementById(
      "threads"
    ).textContent = `${data.process.threads.threadPoolActive}/${data.process.threads.maxWorkerThreads}`;
  } else {
    document.getElementById("threads").textContent = data.process.threads;
  }

  // Disk usage
  const diskElem = document.getElementById("disk");
  if (diskElem) {
    diskElem.textContent = data.diskUsage
      ? `${data.diskUsage.totalGB} GB`
      : "-";
  }
}

function setOfflineStatus() {
  document.getElementById("server-status").textContent = "🔴 Offline";
}

/**
 * ============================================================================
 * VERSIONS MANAGEMENT
 * ============================================================================
 */

async function loadVersions() {
  try {
    const response = await fetch(`${API_BASE}/versions`);
    if (!response.ok) throw new Error(`HTTP ${response.status}`);

    const data = await response.json();
    updateVersionBadges(data);
    updateVersionTable("v6", data.v6.versions, data.v6.active);
    updateVersionTable(
      "v7",
      data.v7.versions,
      data.v7.activeFixed,
      data.v7.activeLatest
    );
    populateVersionSelect();
  } catch (error) {
    console.error("Error loading versions:", error);
  }
}

function updateVersionBadges(data) {
  document.getElementById("v6-active").textContent = data.v6.active || "-";
  document.getElementById("v7-fixed").textContent = data.v7.activeFixed || "-";
  document.getElementById("v7-latest").textContent =
    data.v7.activeLatest || "-";
}

function updateVersionTable(branch, versions, ...active) {
  const tbody = document.getElementById(`${branch}-list`);
  tbody.innerHTML = "";

  versions.forEach((version) => {
    const isActive = active.includes(version);
    const row = document.createElement("tr");

    if (branch === "v6") {
      row.innerHTML = renderV6Row(version, isActive);
    } else {
      row.innerHTML = renderV7Row(version, isActive, active);
    }

    tbody.appendChild(row);
  });
}

function renderV6Row(version, isActive) {
  const statusBadge = `<span class="status-badge ${
    isActive ? "active" : "inactive"
  }">
    ${isActive ? "✓" : "✗"}
  </span>`;

  const deleteBtn = !isActive
    ? `<button class="btn-delete" onclick="removeVersion('${version}')">Delete</button>`
    : "";

  return `
    <td><strong>${version}</strong></td>
    <td>${statusBadge}</td>
    <td>
      <button class="btn-set" onclick="setVersion('${version}')">Set</button>
      ${deleteBtn}
    </td>
  `;
}

function renderV7Row(version, isActive, active) {
  const isFixed = version === active[0];
  const isLatest = version === active[1];
  const type = isFixed ? "Fixed" : isLatest ? "Latest" : "";

  const statusBadge = `<span class="status-badge ${
    isActive ? "active" : "inactive"
  }">
    ${isActive ? "✓" : "✗"}
  </span>`;

  const deleteBtn = !isActive
    ? `<button class="btn-delete" onclick="removeVersion('${version}')">Delete</button>`
    : "";

  return `
    <td><strong>${version}</strong></td>
    <td>${type}</td>
    <td>${statusBadge}</td>
    <td>
      <button class="btn-set" onclick="setVersion('${version}')">Set</button>
      ${deleteBtn}
    </td>
  `;
}

async function setVersion(version) {
  try {
    const response = await fetch(`${API_BASE}/set-active-version/${version}`, {
      method: "POST",
    });

    if (!response.ok) {
      const error = await response.json();
      showToast(`Error: ${error.message || error.code}`, "error");
      return;
    }

    showToast(`Version ${version} set as active`, "success");
    await loadVersions();
  } catch (error) {
    console.error("Set version error:", error);
    showToast(`Error: ${error.message}`, "error");
  }
}

async function removeVersion(version) {
  if (!confirm(`Delete version ${version}?`)) return;

  try {
    const response = await fetch(`${API_BASE}/remove-version/${version}`, {
      method: "DELETE",
    });

    if (!response.ok) {
      const error = await response.json();
      showToast(`Error: ${error.message || error.code}`, "error");
      return;
    }

    showToast(`Version ${version} removed`, "success");
    await loadVersions();
  } catch (error) {
    console.error("Remove version error:", error);
    showToast(`Error: ${error.message}`, "error");
  }
}

async function checkUpdates(event) {
  const btn = event.target;
  const originalText = btn.textContent;
  btn.disabled = true;
  btn.textContent = "⟳ Checking...";

  try {
    const response = await fetch(`${API_BASE}/update-check`, {
      method: "POST",
    });

    handleCheckUpdatesResponse(response);

    if (response.ok) {
      const result = await response.json();
      showToast(
        `Update check completed! Downloaded: ${result.downloaded} files`,
        "success"
      );

      await new Promise((resolve) => setTimeout(resolve, 1000));
      await loadDashboard();
      await loadVersions();
    }
  } catch (error) {
    console.error("Network error:", error);
    showToast(`Network Error: ${error.message}`, "error");
  } finally {
    btn.disabled = false;
    btn.textContent = originalText;
  }
}

function handleCheckUpdatesResponse(response) {
  const statusHandlers = {
    409: "Update check already in progress",
    503: "Service unavailable",
    504: "Request timeout",
  };

  if (statusHandlers[response.status]) {
    showToast(statusHandlers[response.status], "warning");
  }
}

function populateVersionSelect() {
  const select = document.getElementById("version-select");
  if (!select) return;

  const allVersions = new Set();

  document
    .querySelectorAll("#v6-list tr td:first-child strong")
    .forEach((el) => {
      allVersions.add(el.textContent);
    });

  document
    .querySelectorAll("#v7-list tr td:first-child strong")
    .forEach((el) => {
      allVersions.add(el.textContent);
    });

  const currentValue = select.value;
  select.innerHTML = '<option value="">Select a version...</option>';

  Array.from(allVersions)
    .sort()
    .forEach((version) => {
      const option = document.createElement("option");
      option.value = version;
      option.textContent = version;
      select.appendChild(option);
    });

  if (currentValue && allVersions.has(currentValue)) {
    select.value = currentValue;
  }
}

/**
 * ============================================================================
 * LOGS MANAGEMENT
 * ============================================================================
 */

let searchTimeout = null;

function debounceLoadLogs() {
  clearTimeout(searchTimeout);
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
    autoRefreshInterval = setInterval(loadLogs, 10000);
    showToast("Auto-refresh enabled", "info");
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
    displayLogsError(error.message);
  }
}

function displayLogs(logs) {
  const container = document.getElementById("logs-content");

  if (!logs || logs.length === 0) {
    container.innerHTML = '<div class="log-entry">No logs found</div>';
    return;
  }

  container.innerHTML = logs.map((log) => renderLogEntry(log)).join("");
}

function renderLogEntry(log) {
  const levelClass = log.level.toLowerCase();
  const exceptionIndicator = log.exception
    ? '<span class="exception-indicator" title="Contains exception">⚠️</span>'
    : "";

  return `
    <div class="log-entry ${levelClass}">
      <span class="log-col timestamp">${formatDateTime(log.timestamp)}</span>
      <span class="log-col level">
        <span class="level-badge ${levelClass}">${log.level}</span>
      </span>
      <span class="log-col source" title="${escapeHtml(log.source)}">
        ${truncateText(log.source, 30)}
      </span>
      <span class="log-col message" title="${escapeHtml(log.message)}">
        ${escapeHtml(truncateText(log.message, 100))}
        ${exceptionIndicator}
      </span>
    </div>
  `;
}

function displayLogsError(message) {
  document.getElementById(
    "logs-content"
  ).innerHTML = `<div class="log-entry error">Error loading logs: ${escapeHtml(
    message
  )}</div>`;
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

async function downloadLogs() {
  try {
    const response = await fetch(`${API_BASE}/logs/download`);
    if (!response.ok) throw new Error(`HTTP ${response.status}`);

    const blob = await response.blob();
    const fileName = `logs-${new Date()
      .toISOString()
      .slice(0, 19)
      .replace(/:/g, "-")}.zip`;
    downloadBlob(blob, fileName);
    showToast("Logs downloaded successfully", "success");
  } catch (error) {
    console.error("Error downloading logs:", error);
    showToast(`Error downloading logs: ${error.message}`, "error");
  }
}

function downloadBlob(blob, fileName) {
  const url = window.URL.createObjectURL(blob);
  const link = document.createElement("a");
  link.href = url;
  link.download = fileName;
  document.body.appendChild(link);
  link.click();
  document.body.removeChild(link);
  window.URL.revokeObjectURL(url);
}

/**
 * ============================================================================
 * SCHEDULE MANAGEMENT
 * ============================================================================
 */

async function loadSchedule() {
  const scheduleForm = document.getElementById("schedule-form");
  if (!scheduleForm) {
    console.warn("Schedule form not found");
    return;
  }

  try {
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
  const statusBadge = document.getElementById("schedule-status-badge");
  statusBadge.textContent = status.status;
  statusBadge.className = `status-badge ${status.status.toLowerCase()}`;

  document.getElementById("next-check-time").textContent =
    status.nextScheduledCheck
      ? formatDateTime(status.nextScheduledCheck)
      : "Never";

  document.getElementById("time-until-check").textContent =
    status.timeUntilNextCheck ? formatTimeSpan(status.timeUntilNextCheck) : "-";

  document.getElementById("paused-until").textContent = status.config
    ?.pausedUntil
    ? formatDateTime(status.config.pausedUntil)
    : "Not paused";

  // Update form
  document.getElementById("schedule-enabled").checked = config.enabled;
  document.getElementById("check-time").value = config.checkTime.substring(
    0,
    5
  );
  document.getElementById("check-interval").value = config.intervalMinutes;
  document.getElementById("notify-completion").checked =
    config.notifyOnCompletion;
  document.getElementById("notify-errors").checked = config.notifyOnError;

  // Update days checkboxes
  document.querySelectorAll('input[name="days"]').forEach((checkbox) => {
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
    intervalMinutes: parseInt(formData.get("intervalMinutes"), 10),
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
    await loadSchedule();
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
    await loadSchedule();
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
    await loadSchedule();
  } catch (error) {
    console.error("Error resuming schedule:", error);
    showToast(`Error resuming schedule: ${error.message}`, "error");
  }
}

/**
 * ============================================================================
 * CHANGELOG MANAGEMENT
 * ============================================================================
 */

async function loadGlobalChangelog() {
  const contentDiv = document.getElementById("global-changelog-content");
  contentDiv.innerHTML = '<p style="color: #999;">Loading...</p>';

  try {
    const response = await fetch(`${API_BASE}/changelog`);

    if (response.status === 404) {
      contentDiv.innerHTML =
        '<p style="color: #999;">Global changelog not available yet. Downloads will be started soon.</p>';
      return;
    }

    if (!response.ok) throw new Error(`HTTP ${response.status}`);

    const text = await response.text();
    contentDiv.innerHTML = `<pre>${escapeHtml(text)}</pre>`;
  } catch (error) {
    console.error("Error loading global changelog:", error);
    contentDiv.innerHTML = `<p style="color: #d32f2f;">Error loading changelog: ${escapeHtml(
      error.message
    )}</p>`;
  }
}

async function loadVersionChangelog() {
  const select = document.getElementById("version-select");
  const version = select.value;

  if (!version) {
    showToast("Please select a version", "warning");
    return;
  }

  const contentDiv = document.getElementById("version-changelog-content");
  contentDiv.innerHTML = '<p style="color: #999;">Loading...</p>';

  try {
    const response = await fetch(`${API_BASE}/changelog/${version}`);

    if (response.status === 404) {
      contentDiv.innerHTML = `<p style="color: #999;">Changelog not available for version ${version}</p>`;
      return;
    }

    if (!response.ok) throw new Error(`HTTP ${response.status}`);

    const text = await response.text();
    contentDiv.innerHTML = `<pre>${escapeHtml(text)}</pre>`;
  } catch (error) {
    console.error("Error loading version changelog:", error);
    contentDiv.innerHTML = `<p style="color: #d32f2f;">Error loading changelog: ${escapeHtml(
      error.message
    )}</p>`;
  }
}

async function loadVersionHistory() {
  const contentDiv = document.getElementById("history-list");
  contentDiv.innerHTML =
    '<tr><td colspan="4" style="text-align: center; color: #999;">Loading...</td></tr>';

  try {
    const response = await fetch(`${API_BASE}/versions/history?take=50`);
    if (!response.ok) throw new Error(`HTTP ${response.status}`);

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
  } catch (error) {
    console.error("Error loading version history:", error);
    contentDiv.innerHTML = `<tr><td colspan="4" style="text-align: center; color: #d32f2f;">Error: ${escapeHtml(
      error.message
    )}</td></tr>`;
  }
}

/**
 * ============================================================================
 * CONFIGURATION MANAGEMENT
 * ============================================================================
 */

async function loadAllowedArches() {
  const container = document.getElementById("arches-container");
  if (!container) return;

  try {
    const response = await fetch(`${API_BASE}/settings/arches`);
    if (!response.ok) {
      console.warn("Failed to load allowed arches:", response.status);
      return;
    }

    const arches = await response.json();
    const archSet = new Set(arches.map((a) => a.toLowerCase()));

    container.querySelectorAll('input[type="checkbox"]').forEach((cb) => {
      cb.checked = archSet.has(cb.value.toLowerCase());
    });

    const statusElem = document.getElementById("arches-status");
    if (statusElem) {
      statusElem.textContent =
        arches.length > 0
          ? `Loaded: ${arches.join(", ")}`
          : "Loaded default architectures";
    }
  } catch (error) {
    console.error("Error loading allowed arches:", error);
    showToast(`Error loading architectures: ${error.message}`, "error");
  }
}

async function saveAllowedArches() {
  const container = document.getElementById("arches-container");
  if (!container) return;

  const selected = Array.from(
    container.querySelectorAll('input[type="checkbox"]:checked')
  ).map((cb) => cb.value);

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

    const statusElem = document.getElementById("arches-status");
    if (statusElem) {
      statusElem.textContent =
        selected.length > 0
          ? `Saved: ${selected.join(", ")}`
          : "Saved: default architectures will be used";
    }
  } catch (error) {
    console.error("Error saving allowed arches:", error);
    showToast(`Error saving architectures: ${error.message}`, "error");
  }
}

/**
 * ============================================================================
 * TAB SWITCHING
 * ============================================================================
 */

function switchTab(event, tabName) {
  event.preventDefault();

  // Update tab panes
  document
    .querySelectorAll(".tab-pane")
    .forEach((pane) => pane.classList.remove("active"));
  document.getElementById(tabName).classList.add("active");

  // Update nav links
  document
    .querySelectorAll(".nav-link")
    .forEach((link) => link.classList.remove("active"));
  event.target.classList.add("active");

  // Load tab-specific data
  handleTabSwitch(tabName);
}

function handleTabSwitch(tabName) {
  switch (tabName) {
    case "logs":
      loadLogs();
      break;
    case "schedule":
      loadSchedule();
      break;
    case "config":
      loadAllowedArches();
      break;
    case "changelog":
      loadGlobalChangelog();
      break;
  }
}

function switchVersionTab(event, tabName) {
  event.preventDefault();
  document
    .querySelectorAll(".version-tab")
    .forEach((tab) => tab.classList.remove("active"));
  document
    .querySelectorAll(".tab-btn")
    .forEach((btn) => btn.classList.remove("active"));

  document.getElementById(tabName).classList.add("active");
  event.target.classList.add("active");
}

function switchChangelogTab(event, tabName) {
  event.preventDefault();
  document
    .querySelectorAll(".changelog-tab")
    .forEach((tab) => tab.classList.remove("active"));
  document
    .querySelectorAll(".changelog-tab")
    .forEach((btn) => btn.classList.remove("active"));

  document.getElementById(tabName).classList.add("active");
  event.target.classList.add("active");

  // Load data for selected tab
  loadChangelogTabData(tabName);
}

function loadChangelogTabData(tabName) {
  if (tabName === "global-changelog") {
    loadGlobalChangelog();
  } else if (tabName === "version-changelog") {
    // Version changelog loads on button click in UI
  } else if (tabName === "history") {
    loadVersionHistory();
  }
}

/**
 * ============================================================================
 * PERIODIC UPDATES
 * ============================================================================
 */

function startPeriodicUpdates() {
  // Status update every 5 seconds
  if (timers.status) clearInterval(timers.status);
  timers.status = setInterval(loadDashboard, INTERVALS.STATUS);

  // Versions update every 60 seconds
  if (timers.versions) clearInterval(timers.versions);
  timers.versions = setInterval(loadVersions, INTERVALS.VERSIONS);
}

function stopPeriodicUpdates() {
  Object.values(timers).forEach((timer) => {
    if (timer) clearInterval(timer);
  });
}

/**
 * ============================================================================
 * UTILITY FUNCTIONS
 * ============================================================================
 */

function formatDateTime(dateString) {
  return new Date(dateString).toLocaleString();
}

function formatDate(dateString) {
  return new Date(dateString).toLocaleDateString();
}

function formatTimeSpan(milliseconds) {
  const seconds = Math.floor(milliseconds / 1000);
  const hours = Math.floor(seconds / 3600);
  const minutes = Math.floor((seconds % 3600) / 60);

  return hours > 0 ? `${hours}h ${minutes}m` : `${minutes}m`;
}

function truncateText(text, maxLength) {
  if (!text) return "";
  return text.length > maxLength ? text.substring(0, maxLength) + "..." : text;
}

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

/**
 * Toast notifications
 */
function showToast(message, type = "info") {
  const toast = document.createElement("div");
  toast.className = `toast toast-${type}`;
  toast.textContent = message;

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

// Cleanup on page unload
window.addEventListener("beforeunload", stopPeriodicUpdates);
