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
  const v6Active = data.v6.active
    ? data.v6.active.replace(/[^\d.-]/g, "").trim()
    : "-";
  const v7Fixed = data.v7.activeFixed
    ? data.v7.activeFixed.replace(/[^\d.-]/g, "").trim()
    : "-";
  const v7Latest = data.v7.activeLatest
    ? data.v7.activeLatest.replace(/[^\d.-]/g, "").trim()
    : "-";

  document.getElementById("v6-active").textContent = v6Active;
  document.getElementById("v7-fixed").textContent = v7Fixed;
  document.getElementById("v7-latest").textContent = v7Latest;
}

function updateVersionTable(branch, versions, ...active) {
  const tbody = document.getElementById(`${branch}-list`);
  tbody.innerHTML = "";

  versions.forEach((version) => {
    const cleanVersion = version.replace(/[^\d.-]/g, "").trim();
    const cleanActive = active.map((a) =>
      a ? a.replace(/[^\d.-]/g, "").trim() : a
    );
    const isActive = cleanActive.includes(cleanVersion);
    const row = document.createElement("tr");

    if (branch === "v6") {
      row.innerHTML = renderV6Row(version, isActive);
    } else {
      row.innerHTML = renderV7Row(version, isActive, cleanActive);
    }

    tbody.appendChild(row);
  });
}

function renderV6Row(version, isActive) {
  const cleanVersion = version.replace(/[^\d.-]/g, "").trim();
  const statusBadge = `<span class="status-badge ${
    isActive ? "active" : "inactive"
  }">
    ${isActive ? "✓" : "✗"}
  </span>`;

  const deleteBtn = !isActive
    ? `<button class="btn-delete" onclick="removeVersion('${cleanVersion}')">Delete</button>`
    : "";

  return `
    <td title="${version}" style="cursor: pointer;" onclick="copyVersionToClipboard('${cleanVersion}')">
      <strong data-version="${cleanVersion}">📋 ${version}</strong>
    </td>
    <td>${statusBadge}</td>
    <td>
      <button class="btn-set" onclick="setVersion('${cleanVersion}')">Set</button>
      ${deleteBtn}
    </td>
  `;
}

function renderV7Row(version, isActive, active) {
  const cleanVersion = version.replace(/[^\d.-]/g, "").trim();
  const isFixed = cleanVersion === active[0];
  const isLatest = cleanVersion === active[1];
  const type = isFixed ? "Fixed" : isLatest ? "Latest" : "";

  const statusBadge = `<span class="status-badge ${
    isActive ? "active" : "inactive"
  }">
    ${isActive ? "✓" : "✗"}
  </span>`;

  const deleteBtn = !isActive
    ? `<button class="btn-delete" onclick="removeVersion('${cleanVersion}')">Delete</button>`
    : "";

  return `
    <td title="${version}" style="cursor: pointer;" onclick="copyVersionToClipboard('${cleanVersion}')">
      <strong data-version="${cleanVersion}">${version}</strong>
    </td>
    <td>${type}</td>
    <td>${statusBadge}</td>
    <td>
      <button class="btn-set" onclick="setVersion('${cleanVersion}')">Set</button>
      ${deleteBtn}
    </td>
  `;
}

async function setVersion(version) {
  const cleanVersion = version.replace(/[^\d.-]/g, "").trim();

  try {
    const response = await fetch(
      `${API_BASE}/set-active-version/${encodeURIComponent(cleanVersion)}`,
      {
        method: "POST",
      }
    );

    if (!response.ok) {
      const error = await response.json();
      showToast(`Error: ${error.message || error.code}`, "error");
      return;
    }

    showToast(`Version ${cleanVersion} set as active`, "success");
    await loadVersions();
  } catch (error) {
    console.error("Set version error:", error);
    showToast(`Error: ${error.message}`, "error");
  }
}

async function removeVersion(version) {
  const cleanVersion = version.replace(/[^\d.-]/g, "").trim();

  if (!confirm(`Delete version ${cleanVersion}?`)) return;

  try {
    const response = await fetch(
      `${API_BASE}/remove-version/${encodeURIComponent(cleanVersion)}`,
      {
        method: "DELETE",
      }
    );

    if (!response.ok) {
      const error = await response.json();
      showToast(`Error: ${error.message || error.code}`, "error");
      return;
    }

    showToast(`Version ${cleanVersion} removed`, "success");
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
    .querySelectorAll("#v6-list tr td:first-child strong[data-version]")
    .forEach((el) => {
      allVersions.add(el.getAttribute("data-version"));
    });

  document
    .querySelectorAll("#v7-list tr td:first-child strong[data-version]")
    .forEach((el) => {
      allVersions.add(el.getAttribute("data-version"));
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

  const params = new window.URLSearchParams();
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

  const displayTime = log.timestampLocal || log.timestampUtc || log.timestamp;

  return `
    <div class="log-entry ${levelClass}">
      <span class="log-col timestamp">${formatDateTime(displayTime)}</span>
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

  const oldest =
    stats.oldestEntryLocal || stats.oldestEntryUtc || stats.oldestEntry;
  const newest =
    stats.newestEntryLocal || stats.newestEntryUtc || stats.newestEntry;

  const rangeText =
    oldest && newest
      ? `${formatDate(oldest)} - ${formatDate(newest)}`
      : "No data";

  const tzLabel = stats.timeZone || "UTC";

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
        <span class="stat-label">Time Range (${tzLabel}):</span>
        <span class="stat-value">${rangeText}</span>
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

  document.getElementById("schedule-enabled").checked = config.enabled;
  document.getElementById("check-time").value = config.checkTime.substring(
    0,
    5
  );
  document.getElementById("check-interval").value = config.intervalMinutes;
  document.getElementById("notify-completion").checked =
    config.notifyOnCompletion;
  document.getElementById("notify-errors").checked = config.notifyOnError;

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

  const cleanVersion = version.replace(/[^\d.-]/g, "").trim();

  const contentDiv = document.getElementById("version-changelog-content");
  contentDiv.innerHTML = '<p style="color: #999;">Loading...</p>';

  try {
    const response = await fetch(
      `${API_BASE}/changelog/${encodeURIComponent(cleanVersion)}`
    );

    if (response.status === 404) {
      contentDiv.innerHTML = `<p style="color: #999;">Changelog not available for version ${cleanVersion}</p>`;
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
 * DELETE PREFIXES MANAGEMENT
 * ============================================================================
 */

async function loadDeletePrefixes() {
  const textarea = document.getElementById("delete-prefixes-input");
  const status = document.getElementById("delete-prefixes-status");
  if (!textarea) return;

  try {
    const resp = await fetch(`${API_BASE}/settings/delete-prefixes`);
    if (!resp.ok) {
      throw new Error(`HTTP ${resp.status}`);
    }

    const prefixes = await resp.json(); // array of strings

    // Display on separate lines for easier editing
    textarea.value = prefixes.join("\n");

    if (status) {
      status.textContent =
        prefixes.length > 0
          ? `Loaded ${prefixes.length} prefixes`
          : "No prefixes configured";
      status.className = "config-status";
    }
  } catch (error) {
    console.error("Error loading delete prefixes:", error);
    if (status) {
      status.textContent = `Error loading: ${error.message}`;
      status.className = "config-status error";
    }
  }
}

async function saveDeletePrefixes() {
  const textarea = document.getElementById("delete-prefixes-input");
  const status = document.getElementById("delete-prefixes-status");
  if (!textarea) return;

  // Parse lines and filter empty ones
  const prefixes = textarea.value
    .split("\n")
    .map((s) => s.trim())
    .filter((s) => s.length > 0);

  try {
    const resp = await fetch(`${API_BASE}/settings/delete-prefixes`, {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
      },
      body: JSON.stringify(prefixes),
    });

    if (!resp.ok) {
      let err;
      try {
        err = await resp.json();
      } catch {
        /* ignore */
      }
      throw new Error(err?.message || `HTTP ${resp.status}`);
    }

    let data = null;
    try {
      data = await resp.json();
    } catch {
      /* ignore */
    }

    if (status) {
      status.textContent =
        prefixes.length > 0
          ? `✓ Saved ${prefixes.length} prefix(es)`
          : "✓ List cleared";
      status.className = "config-status success";
    }

    showToast(
      data?.message || `Saved ${prefixes.length} file prefix(es)`,
      "success"
    );
  } catch (error) {
    console.error("Error saving delete prefixes:", error);
    if (status) {
      status.textContent = `✗ Error: ${error.message}`;
      status.className = "config-status error";
    }
    showToast(`Error saving delete prefixes: ${error.message}`, "error");
  }
}

/**
 * ============================================================================
 * TIMEZONE MANAGEMENT
 * ============================================================================
 */

async function loadTimeZones() {
  try {
    const [listResp, currentResp] = await Promise.all([
      fetch(`${API_BASE}/settings/timezone/list`),
      fetch(`${API_BASE}/settings/timezone`),
    ]);

    if (!listResp.ok || !currentResp.ok) {
      throw new Error("Failed to load timezones");
    }

    const zones = await listResp.json();
    const current = await currentResp.json();

    const select = document.getElementById("tzSelect");
    if (!select) return;

    select.innerHTML = "";

    zones.forEach((z) => {
      const opt = document.createElement("option");
      opt.value = z.id;
      opt.textContent = z.displayName || z.id;
      if (z.id === current.id) {
        opt.selected = true;
      }
      select.appendChild(opt);
    });
  } catch (error) {
    console.error("Error loading timezones:", error);
    showToast(`Error loading timezones: ${error.message}`, "error");
  }
}

async function saveTimeZone() {
  try {
    const select = document.getElementById("tzSelect");
    const tzId = select.value;

    const response = await fetch(`${API_BASE}/settings/timezone`, {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
      },
      body: JSON.stringify({ timeZoneId: tzId }),
    });

    if (!response.ok) {
      const error = await response.json();
      throw new Error(error.message || `HTTP ${response.status}`);
    }

    const data = await response.json();
    const statusEl = document.getElementById("tzStatus");
    statusEl.textContent = `✓ Saved: ${data.id}`;
    statusEl.style.color = "var(--success)";

    showToast(`Time zone set to: ${data.id}`, "success");
  } catch (error) {
    console.error("Error saving timezone:", error);
    const statusEl = document.getElementById("tzStatus");
    statusEl.textContent = `✗ Error: ${error.message}`;
    statusEl.style.color = "var(--error)";
    showToast(`Error saving timezone: ${error.message}`, "error");
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
      loadTimeZones();
      loadDeletePrefixes();
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
    .querySelectorAll(".tab-btn")
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
  timers.status = setInterval(() => {
    loadDashboard();
    checkServerHealth(); // Добавляем проверку здоровья
  }, INTERVALS.STATUS);

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
  if (
    milliseconds === null ||
    milliseconds === undefined ||
    isNaN(milliseconds) ||
    milliseconds <= 0
  ) {
    return "Now";
  }

  const seconds = Math.floor(milliseconds / 1000);
  const hours = Math.floor(seconds / 3600);
  const minutes = Math.floor((seconds % 3600) / 60);

  if (hours > 0) {
    return `${hours}h ${minutes}m`;
  } else if (minutes > 0) {
    return `${minutes}m`;
  } else {
    return "Now";
  }
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

/**
 * Export data to JSON
 */
function exportDashboardData() {
  try {
    const data = {
      exportTime: new Date().toISOString(),
      cpuUsage: document.getElementById("cpuUsage").textContent,
      memory: document.getElementById("memory").textContent,
      uptime: document.getElementById("uptime").textContent,
      disk: document.getElementById("disk")?.textContent || "-",
      totalFiles: document.getElementById("total-files").textContent,
      totalGB: document.getElementById("total-gb").textContent,
      serverStatus: document.getElementById("server-status").textContent,
    };

    const dataStr = JSON.stringify(data, null, 2);
    const blob = new Blob([dataStr], { type: "application/json" });
    downloadBlob(
      blob,
      `dashboard-${new Date().toISOString().slice(0, 10)}.json`
    );
    showToast("Dashboard data exported", "success");
  } catch (error) {
    console.error("Error exporting data:", error);
    showToast(`Error exporting data: ${error.message}`, "error");
  }
}

/**
 * ============================================================================
 * REAL-TIME MONITORING
 * ============================================================================
 */

let lastDashboardData = null;

async function loadDashboardWithComparison() {
  try {
    const response = await fetch(`${API_BASE}/status`);
    if (!response.ok) throw new Error(`HTTP ${response.status}`);

    const data = await response.json();
    updateDashboardUI(data);
    detectAnomalies(data);
    lastDashboardData = data;
  } catch (error) {
    console.error("Failed to load dashboard:", error);
    setOfflineStatus();
  }
}

function detectAnomalies(data) {
  const alerts = [];

  // Check CPU usage
  const cpuPercent = parseInt(data.process.cpuUsage);
  if (cpuPercent > 80) {
    alerts.push({ type: "error", message: `High CPU usage: ${cpuPercent}%` });
  }

  // Check memory
  const memoryPercent = parseInt(data.process.memory);
  if (memoryPercent > 85) {
    alerts.push({
      type: "warning",
      message: `High memory usage: ${memoryPercent}%`,
    });
  }

  // Check disk
  if (data.diskUsage && data.diskUsage.totalGB > 900) {
    alerts.push({
      type: "warning",
      message: `Low disk space: ${data.diskUsage.totalGB} GB used`,
    });
  }

  // Display alerts
  if (alerts.length > 0) {
    updateAlertsDisplay(alerts);
  }
}

function updateAlertsDisplay(alerts) {
  const alertDisk = document.getElementById("alert-disk");
  const alertInternet = document.getElementById("alert-internet");
  const alertJobs = document.getElementById("alert-jobs");

  // Сброс всех алертов на OK
  const allAlerts = {
    disk: alertDisk,
    internet: alertInternet,
    jobs: alertJobs,
  };

  Object.values(allAlerts).forEach((el) => {
    if (el) {
      el.textContent = "✓ OK";
      el.style.color = "var(--success)";
      el.title = "All systems operational";
    }
  });

  // Применяем активные алерты
  alerts.forEach((alert) => {
    const targetElement = allAlerts[alert.target];
    if (targetElement) {
      const icon = alert.type === "error" ? "✗" : "⚠";
      targetElement.textContent = `${icon} ${
        alert.type === "error" ? "Error" : "Warning"
      }`;
      targetElement.style.color =
        alert.type === "error" ? "var(--error)" : "var(--warning)";
      targetElement.title = alert.message;
    }
  });
}

/**
 * ============================================================================
 * STATISTICS & ANALYTICS
 * ============================================================================
 */

function getLogStats() {
  try {
    const statsItems = document.querySelectorAll("#logs-stats .stat-item");

    let total = 0,
      info = 0,
      warning = 0,
      error = 0;

    statsItems.forEach((item) => {
      const label =
        item.querySelector(".stat-label")?.textContent.toLowerCase() || "";
      const number = parseInt(
        item.querySelector(".stat-number")?.textContent || "0"
      );

      if (label.includes("total")) total = number;
      else if (label.includes("info")) info = number;
      else if (label.includes("warning")) warning = number;
      else if (label.includes("error")) error = number;
    });

    return { total, info, warning, error };
  } catch (error) {
    console.error("Error getting log stats:", error);
    return null;
  }
}

function displayLogAnalytics() {
  const stats = getLogStats();

  if (!stats || stats.total === 0) {
    showToast("No log data available for analysis", "info");
    return;
  }

  const total = stats.total || 1; // Избегаем деления на ноль
  const errorPercent = ((stats.error / total) * 100).toFixed(1);
  const warningPercent = ((stats.warning / total) * 100).toFixed(1);
  const infoPercent = ((stats.info / total) * 100).toFixed(1);

  // Определяем статус здоровья
  let healthStatus = "Excellent";
  let healthColor = "var(--success)";

  if (stats.error > stats.warning) {
    healthStatus = "Critical";
    healthColor = "var(--error)";
  } else if (parseFloat(errorPercent) > 5) {
    healthStatus = "Poor";
    healthColor = "var(--error)";
  } else if (parseFloat(warningPercent) > 20) {
    healthStatus = "Fair";
    healthColor = "var(--warning)";
  } else if (parseFloat(warningPercent) > 10) {
    healthStatus = "Good";
    healthColor = "var(--info)";
  }

  // Создаём модальное окно с аналитикой
  const modal = createAnalyticsModal(stats, {
    errorPercent,
    warningPercent,
    infoPercent,
    healthStatus,
    healthColor,
  });

  document.body.appendChild(modal);

  // Показываем также краткий тост
  showToast(
    `Health: ${healthStatus} | Errors: ${errorPercent}% | Warnings: ${warningPercent}%`,
    stats.error > stats.warning
      ? "error"
      : stats.warning > stats.info / 2
      ? "warning"
      : "success"
  );
}

function createAnalyticsModal(stats, percentages) {
  const modal = document.createElement("div");
  modal.className = "analytics-modal";
  modal.innerHTML = `
    <div class="analytics-content">
      <div class="analytics-header">
        <h3>📊 Log Analytics Report</h3>
        <button class="analytics-close" onclick="this.closest('.analytics-modal').remove()">✕</button>
      </div>
      <div class="analytics-body">
        <div class="analytics-health" style="border-color: ${
          percentages.healthColor
        }">
          <div class="health-title">System Health</div>
          <div class="health-status" style="color: ${
            percentages.healthColor
          }">${percentages.healthStatus}</div>
        </div>

        <div class="analytics-grid">
          <div class="analytics-stat">
            <div class="stat-icon">📝</div>
            <div class="stat-title">Total Entries</div>
            <div class="stat-value">${stats.total}</div>
            <div class="stat-percent">100%</div>
          </div>

          <div class="analytics-stat info">
            <div class="stat-icon">ℹ️</div>
            <div class="stat-title">Information</div>
            <div class="stat-value">${stats.info}</div>
            <div class="stat-percent">${percentages.infoPercent}%</div>
          </div>

          <div class="analytics-stat warning">
            <div class="stat-icon">⚠️</div>
            <div class="stat-title">Warnings</div>
            <div class="stat-value">${stats.warning}</div>
            <div class="stat-percent">${percentages.warningPercent}%</div>
          </div>

          <div class="analytics-stat error">
            <div class="stat-icon">❌</div>
            <div class="stat-title">Errors</div>
            <div class="stat-value">${stats.error}</div>
            <div class="stat-percent">${percentages.errorPercent}%</div>
          </div>
        </div>

        <div class="analytics-recommendations">
          <h4>💡 Recommendations</h4>
          <ul>
            ${generateRecommendations(stats, percentages)}
          </ul>
        </div>
      </div>
    </div>
  `;

  return modal;
}

function generateRecommendations(stats, percentages) {
  const recommendations = [];

  if (parseFloat(percentages.errorPercent) > 5) {
    recommendations.push(
      '<li class="rec-error">⚠️ High error rate detected. Review error logs immediately.</li>'
    );
  }

  if (parseFloat(percentages.warningPercent) > 20) {
    recommendations.push(
      '<li class="rec-warning">⚠️ Many warnings detected. Consider investigating common issues.</li>'
    );
  }

  if (stats.error > stats.warning) {
    recommendations.push(
      '<li class="rec-error">⚠️ Errors exceed warnings - system may be unstable.</li>'
    );
  }

  if (
    parseFloat(percentages.errorPercent) < 1 &&
    parseFloat(percentages.warningPercent) < 5
  ) {
    recommendations.push(
      '<li class="rec-success">✓ System is operating normally with minimal issues.</li>'
    );
  }

  if (stats.total > 1000) {
    recommendations.push(
      '<li class="rec-info">ℹ️ Large log volume. Consider log rotation or cleanup.</li>'
    );
  }

  if (recommendations.length === 0) {
    recommendations.push(
      '<li class="rec-success">✓ No immediate issues detected.</li>'
    );
  }

  return recommendations.join("");
}

/**
 * ============================================================================
 * KEYBOARD SHORTCUTS
 * ============================================================================
 */

document.addEventListener("keydown", (event) => {
  // Ctrl/Cmd + K = Open search
  if ((event.ctrlKey || event.metaKey) && event.key === "k") {
    event.preventDefault();
    focusSearch();
  }

  // Ctrl/Cmd + R = Refresh current tab
  if ((event.ctrlKey || event.metaKey) && event.key === "r") {
    event.preventDefault();
    refreshCurrentTab();
  }

  // Ctrl/Cmd + S = Save (for schedule/config)
  if ((event.ctrlKey || event.metaKey) && event.key === "s") {
    event.preventDefault();
    const scheduleForm = document.getElementById("schedule-form");
    if (scheduleForm?.offsetParent !== null) {
      saveSchedule({ preventDefault: () => {} });
    }
  }
});

function focusSearch() {
  const searchInput = document.getElementById("log-search");
  if (searchInput) {
    searchInput.focus();
    searchInput.select();
    showToast("Search focused (Ctrl+K)", "info");
  }
}

function refreshCurrentTab() {
  const activeTab = document.querySelector(".tab-pane.active");
  if (!activeTab) return;

  const tabId = activeTab.id;

  if (tabId === "dashboard") {
    loadDashboard();
    loadVersions();
  } else if (tabId === "versions") {
    loadVersions();
  } else if (tabId === "logs") {
    loadLogs();
  } else if (tabId === "schedule") {
    loadSchedule();
  } else if (tabId === "changelog") {
    loadGlobalChangelog();
  }

  showToast("Tab refreshed", "info");
}

/**
 * ============================================================================
 * ALERTS & NOTIFICATIONS
 * ============================================================================
 */

function setupErrorAlert() {
  window.addEventListener("error", (event) => {
    console.error("Global error:", event.error);
    showToast(`Error: ${event.error?.message || "Unknown error"}`, "error");
  });
}

function checkServerHealth() {
  const status = document.getElementById("server-status")?.textContent || "";

  if (status.includes("Offline")) {
    updateAlertsDisplay([
      { type: "error", message: "Server is offline", target: "internet" },
    ]);
    return false;
  }

  // Проверяем CPU
  const cpuText = document.getElementById("cpuUsage")?.textContent || "0%";
  const cpuPercent = parseInt(cpuText);

  // Проверяем память
  const memoryText = document.getElementById("memory")?.textContent || "0%";
  const memoryPercent = parseInt(memoryText);

  // Проверяем диск
  const diskText = document.getElementById("disk")?.textContent || "0";
  const diskGB = parseFloat(diskText);

  const alerts = [];

  if (cpuPercent > 80) {
    alerts.push({
      type: "error",
      message: `High CPU usage: ${cpuPercent}%`,
      target: "jobs",
    });
  } else if (cpuPercent > 60) {
    alerts.push({
      type: "warning",
      message: `Elevated CPU usage: ${cpuPercent}%`,
      target: "jobs",
    });
  }

  if (memoryPercent > 85) {
    alerts.push({
      type: "error",
      message: `Critical memory usage: ${memoryPercent}%`,
      target: "jobs",
    });
  } else if (memoryPercent > 70) {
    alerts.push({
      type: "warning",
      message: `High memory usage: ${memoryPercent}%`,
      target: "jobs",
    });
  }

  if (diskGB > 900) {
    alerts.push({
      type: "error",
      message: `Low disk space: ${diskGB} GB used`,
      target: "disk",
    });
  } else if (diskGB > 800) {
    alerts.push({
      type: "warning",
      message: `Disk space warning: ${diskGB} GB used`,
      target: "disk",
    });
  }

  if (alerts.length > 0) {
    updateAlertsDisplay(alerts);
    return false;
  } else {
    // Сбросить все алерты на OK
    updateAlertsDisplay([]);
    return true;
  }
}

/**
 * ============================================================================
 * CLIPBOARD UTILITIES
 * ============================================================================
 */

function copyToClipboard(text) {
  navigator.clipboard
    .writeText(text)
    .then(() => {
      showToast("Copied to clipboard", "success");
    })
    .catch((error) => {
      console.error("Failed to copy:", error);
      showToast("Failed to copy to clipboard", "error");
    });
}

function copyVersionToClipboard(version) {
  copyToClipboard(version);
}

/**
 * ============================================================================
 * THEME & UI PREFERENCES
 * ============================================================================
 */

function initializeTheme() {
  const savedTheme = localStorage.getItem("dashboardTheme");

  const prefersDark =
    window.matchMedia &&
    window.matchMedia("(prefers-color-scheme: dark)").matches;

  const theme = savedTheme || (prefersDark ? "dark" : "light");
  applyTheme(theme);
}

function applyTheme(theme) {
  const root = document.documentElement;

  root.setAttribute("data-theme", theme);

  if (document.body) {
    document.body.classList.remove("theme-dark", "theme-light");
    document.body.classList.add(
      theme === "dark" ? "theme-dark" : "theme-light"
    );
  }

  localStorage.setItem("dashboardTheme", theme);

  const toggleBtn = document.getElementById("themeToggle");
  if (toggleBtn) {
    const isDark = theme === "dark";

    toggleBtn.setAttribute(
      "aria-label",
      isDark ? "Switch to light theme" : "Switch to dark theme"
    );

    const iconSpan = toggleBtn.querySelector(".theme-toggle-icon");
    if (iconSpan) {
      iconSpan.textContent = isDark ? "🌙" : "☀️";
    }
  }
}

function toggleTheme() {
  const current =
    localStorage.getItem("dashboardTheme") ||
    document.documentElement.getAttribute("data-theme") ||
    "dark";

  const newTheme = current === "dark" ? "light" : "dark";
  applyTheme(newTheme);

  showToast(
    newTheme === "dark" ? "Dark theme enabled" : "Light theme enabled",
    "info"
  );
}

/**
 * ============================================================================
 * INITIALIZATION
 * ============================================================================
 */

// Initialize theme on page load
initializeTheme();
setupErrorAlert();

// Replace loadDashboard with comparison version
const originalLoadDashboard = loadDashboard;
loadDashboard = loadDashboardWithComparison;
