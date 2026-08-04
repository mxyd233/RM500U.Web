"use strict";

const state = {
  activePage: "dashboard",
  activeTool: "at",
  status: null,
  config: null,
  apn: null,
  apnSavedSnapshot: "",
  sms: null,
  smsLoadTask: null,
  selectedPeer: "",
  webhook: null,
  bands: null,
  cells: null,
  logs: [],
  statusLoading: false,
  pendingRequests: 0,
  pollTimer: null,
  smsPollTimer: null,
  smsRefreshTimer: null
};

const pageMeta = {
  dashboard: ["设备状态", "运行概览"],
  connection: ["PROFILE", "连接与 APN"],
  radio: ["RADIO", "无线参数"],
  sms: ["MESSAGING", "短信会话"],
  tools: ["DIAGNOSTICS", "终端与日志"]
};

const $ = (selector, root = document) => root.querySelector(selector);
const $$ = (selector, root = document) => [...root.querySelectorAll(selector)];

let customSelectSequence = 0;
const customSelectInstances = new WeakMap();
let customSelectDocumentEventsBound = false;

function initCustomSelects(root = document) {
  $$('select:not(.custom-select-native)', root).forEach(enhanceCustomSelect);
  if (customSelectDocumentEventsBound) return;
  customSelectDocumentEventsBound = true;
  document.addEventListener("click", event => {
    if (event.target instanceof Element && event.target.closest(".custom-select")) return;
    closeAllCustomSelects();
  });
  document.addEventListener("focusin", event => {
    if (event.target instanceof Element && event.target.closest(".custom-select")) return;
    closeAllCustomSelects();
  });
}

function enhanceCustomSelect(select) {
  if (!(select instanceof HTMLSelectElement) || customSelectInstances.has(select)) return;
  const wrapper = document.createElement("div");
  wrapper.className = "custom-select";
  const trigger = document.createElement("button");
  trigger.type = "button";
  trigger.className = "custom-select-trigger";
  trigger.setAttribute("aria-haspopup", "listbox");
  trigger.setAttribute("aria-expanded", "false");
  const label = document.createElement("span");
  label.className = "custom-select-value";
  const chevron = document.createElement("span");
  chevron.className = "custom-select-chevron";
  chevron.setAttribute("aria-hidden", "true");
  trigger.append(label, chevron);

  const menu = document.createElement("div");
  menu.className = "custom-select-menu";
  menu.id = `custom-select-menu-${++customSelectSequence}`;
  menu.setAttribute("role", "listbox");
  menu.hidden = true;
  trigger.setAttribute("aria-controls", menu.id);

  const instance = { wrapper, trigger, label, menu, activeIndex: -1 };
  customSelectInstances.set(select, instance);
  select.classList.add("custom-select-native");
  select.setAttribute("aria-hidden", "true");
  select.tabIndex = -1;
  select.parentNode.insertBefore(wrapper, select);
  wrapper.append(select, trigger, menu);

  trigger.addEventListener("click", () => {
    if (wrapper.classList.contains("is-open")) closeCustomSelect(select, true);
    else openCustomSelect(select);
  });
  trigger.addEventListener("keydown", event => handleCustomSelectKeydown(select, event));
  menu.addEventListener("click", event => {
    const option = event.target instanceof Element ? event.target.closest("[data-custom-option-index]") : null;
    if (!option || option.disabled) return;
    chooseCustomSelect(select, Number(option.dataset.customOptionIndex));
  });
  select.addEventListener("input", () => syncCustomSelect(select));
  select.addEventListener("change", () => syncCustomSelect(select));
  const observer = new MutationObserver(() => syncCustomSelect(select));
  observer.observe(select, { childList: true, subtree: true, attributes: true, attributeFilter: ["disabled", "label", "value"] });
  instance.observer = observer;
  syncCustomSelect(select);
}

function syncCustomSelect(select) {
  const instance = customSelectInstances.get(select);
  if (!instance) return;
  const options = [...select.options];
  const selectedIndex = select.selectedIndex;
  const selected = options[selectedIndex];
  instance.label.textContent = selected?.textContent?.trim() || "请选择";
  instance.trigger.disabled = select.disabled || options.length === 0;
  instance.trigger.setAttribute("aria-disabled", String(instance.trigger.disabled));
  instance.menu.replaceChildren();
  options.forEach((option, index) => {
    const item = document.createElement("button");
    item.type = "button";
    item.className = "custom-select-option";
    item.id = `${instance.menu.id}-option-${index}`;
    item.dataset.customOptionIndex = String(index);
    item.setAttribute("role", "option");
    item.setAttribute("aria-selected", String(index === selectedIndex));
    item.disabled = option.disabled;
    item.textContent = option.textContent?.trim() || " ";
    instance.menu.append(item);
  });
  instance.activeIndex = selectedIndex >= 0 ? selectedIndex : findSelectableIndex(options, 0, 1);
  if (instance.wrapper.classList.contains("is-open")) {
    instance.menu.hidden = false;
    highlightCustomSelect(select, instance.activeIndex);
  }
}

function findSelectableIndex(options, start, step) {
  if (!options.length) return -1;
  let index = start;
  for (let count = 0; count < options.length; count += 1) {
    if (index < 0) index = options.length - 1;
    if (index >= options.length) index = 0;
    if (!options[index].disabled) return index;
    index += step;
  }
  return -1;
}

function openCustomSelect(select) {
  const instance = customSelectInstances.get(select);
  if (!instance || instance.trigger.disabled) return;
  closeAllCustomSelects(select);
  instance.wrapper.classList.add("is-open");
  instance.menu.hidden = false;
  instance.activeIndex = select.selectedIndex >= 0 ? select.selectedIndex : findSelectableIndex([...select.options], 0, 1);
  instance.trigger.setAttribute("aria-expanded", "true");
  highlightCustomSelect(select, instance.activeIndex);
}

function closeCustomSelect(select, restoreFocus = false) {
  const instance = customSelectInstances.get(select);
  if (!instance || !instance.wrapper.classList.contains("is-open")) return;
  instance.wrapper.classList.remove("is-open");
  instance.menu.hidden = true;
  instance.trigger.setAttribute("aria-expanded", "false");
  instance.trigger.removeAttribute("aria-activedescendant");
  if (restoreFocus) instance.trigger.focus();
}

function closeAllCustomSelects(exceptSelect = null) {
  $$(".custom-select.is-open").forEach(wrapper => {
    const select = $("select.custom-select-native", wrapper);
    if (select && select !== exceptSelect) closeCustomSelect(select);
  });
}

function highlightCustomSelect(select, index) {
  const instance = customSelectInstances.get(select);
  if (!instance) return;
  const options = [...select.options];
  if (index < 0 || index >= options.length || options[index].disabled) return;
  instance.activeIndex = index;
  $$(".custom-select-option", instance.menu).forEach((item, itemIndex) => item.classList.toggle("is-highlighted", itemIndex === index));
  const active = instance.menu.children[index];
  if (active) {
    instance.trigger.setAttribute("aria-activedescendant", active.id);
    active.scrollIntoView({ block: "nearest" });
  }
}

function moveCustomSelectHighlight(select, step) {
  const instance = customSelectInstances.get(select);
  if (!instance) return;
  const next = findSelectableIndex([...select.options], instance.activeIndex + step, step);
  if (next >= 0) highlightCustomSelect(select, next);
}

function chooseCustomSelect(select, index) {
  const option = select.options[index];
  if (!option || option.disabled) return;
  const changed = select.selectedIndex !== index;
  select.selectedIndex = index;
  syncCustomSelect(select);
  closeCustomSelect(select, true);
  if (changed) {
    select.dispatchEvent(new Event("input", { bubbles: true }));
    select.dispatchEvent(new Event("change", { bubbles: true }));
  }
}

function handleCustomSelectKeydown(select, event) {
  const instance = customSelectInstances.get(select);
  if (!instance || instance.trigger.disabled) return;
  const isOpen = instance.wrapper.classList.contains("is-open");
  if (event.key === "Tab") {
    closeCustomSelect(select);
    return;
  }
  if (event.key === "Escape") {
    if (isOpen) {
      event.preventDefault();
      closeCustomSelect(select, true);
    }
    return;
  }
  if (event.key === "ArrowDown" || event.key === "ArrowUp") {
    event.preventDefault();
    const step = event.key === "ArrowDown" ? 1 : -1;
    if (!isOpen) openCustomSelect(select);
    moveCustomSelectHighlight(select, step);
    return;
  }
  if (event.key === "Home" || event.key === "End") {
    event.preventDefault();
    if (!isOpen) openCustomSelect(select);
    const options = [...select.options];
    const start = event.key === "Home" ? 0 : options.length - 1;
    const step = event.key === "Home" ? 1 : -1;
    const next = findSelectableIndex(options, start, step);
    if (next >= 0) highlightCustomSelect(select, next);
    return;
  }
  if (event.key === "Enter" || event.key === " ") {
    event.preventDefault();
    if (!isOpen) openCustomSelect(select);
    else chooseCustomSelect(select, instance.activeIndex);
  }
}

document.addEventListener("DOMContentLoaded", init);

async function init() {
  bindNavigation();
  bindForms();
  bindActions();
  initCustomSelects();
  initTheme();
  const initialPage = location.hash.slice(1);
  navigate(pageMeta[initialPage] ? initialPage : "dashboard", false);
  const results = await Promise.allSettled([loadConfig(), loadCandidates(), refreshStatus(true, true)]);
  if (results.every(result => result.status === "rejected")) {
    toast("无法连接服务", "后端接口没有响应，请检查 RM500U 服务状态。", "error");
  }
  await ensurePageData(state.activePage);
  startPolling();
}

function bindNavigation() {
  $$('[data-page]').forEach(button => button.addEventListener("click", () => navigate(button.dataset.page)));
  window.addEventListener("hashchange", () => {
    const page = location.hash.slice(1);
    if (pageMeta[page] && page !== state.activePage) navigate(page, false);
  });
}

function navigate(page, updateHash = true) {
  if (!pageMeta[page]) return;
  state.activePage = page;
  $$('[data-page]').forEach(button => button.classList.toggle("is-active", button.dataset.page === page));
  $$('[data-page-panel]').forEach(panel => panel.classList.toggle("is-active", panel.dataset.pagePanel === page));
  $("#pageEyebrow").textContent = pageMeta[page][0];
  $("#pageTitle").textContent = pageMeta[page][1];
  document.title = `${pageMeta[page][1]} - RM500U`;
  if (updateHash) history.replaceState(null, "", `#${page}`);
  ensurePageData(page).catch(showLoadError);
}

async function ensurePageData(page) {
  if (page === "connection") await settle([loadCandidates(), loadApn()]);
  if (page === "radio") await settle([loadBands(), loadCells()]);
  if (page === "sms") await settle([loadSms(), loadWebhook()]);
  if (page === "tools" && state.activeTool === "logs") await loadLogs();
}

function bindForms() {
  $("#configForm")?.addEventListener("submit", event => { event.preventDefault(); saveConfig(); });
  $("#smsForm")?.addEventListener("submit", sendSms);
  $("#smsReplyForm")?.addEventListener("submit", sendReply);
  $("#webhookForm")?.addEventListener("submit", event => event.preventDefault());
  $("#atForm")?.addEventListener("submit", sendAtCommand);
  $("#smsContent")?.addEventListener("input", () => setText("smsCharCount", `${[...$("#smsContent").value].length} / 500`));
  $("#smsReplyForm textarea")?.addEventListener("input", event => setText("replyCount", `${[...event.target.value].length} / 500`));
  $("#logLevelFilter")?.addEventListener("change", renderLogs);
  $("#apnProfileSelect")?.addEventListener("change", selectApnProfile);
  $("#apnForm")?.addEventListener("input", updateApnFromForm);
  $("#apnForm")?.addEventListener("change", updateApnFromForm);
  $("#apnSave")?.addEventListener("click", () => saveApnProfiles().catch(showActionError));
  $("#apnApply")?.addEventListener("click", () => applyApn(false).catch(showActionError));
  $("#apnApplyReconnect")?.addEventListener("click", () => applyApn(true).catch(showActionError));
  $("#apnNew")?.addEventListener("click", newApnProfile);
  $("#apnDuplicate")?.addEventListener("click", duplicateApnProfile);
  $("#apnDelete")?.addEventListener("click", deleteApnProfile);
  $("#webhookSave")?.addEventListener("click", () => saveWebhook().catch(showActionError));
  $("#webhookTest")?.addEventListener("click", () => testWebhook().catch(showActionError));
  $("#threadNew")?.addEventListener("click", newSms);
  $("#conversationList")?.addEventListener("click", event => {
    const button = event.target.closest("button[data-peer]");
    if (button) openConversation(button.dataset.peer);
  });
  $("#threadMessages")?.addEventListener("click", event => {
    const button = event.target.closest("button[data-sms-delete]");
    if (button) deleteSmsMessage(button);
  });
  $("#bandGroups")?.addEventListener("click", handleBandAction);
  $("#neighborCellsBody")?.addEventListener("click", handleCellAction);
  $("#lockSummary")?.addEventListener("click", handleUnlockAction);
  $("#cellLockForm")?.addEventListener("submit", submitCellLock);
  $("#simSlotControl")?.addEventListener("click", setSimSlot);
  $("[data-tool-tab]") && $$('[data-tool-tab]').forEach(button => button.addEventListener("click", () => {
    state.activeTool = button.dataset.toolTab;
    $$('[data-tool-tab]').forEach(item => item.classList.toggle("is-active", item === button));
    $$('[data-tool-panel]').forEach(panel => panel.classList.toggle("is-active", panel.dataset.toolPanel === state.activeTool));
    if (state.activeTool === "logs") loadLogs().catch(showLoadError);
  }));
}

function bindActions() {
  $("#refreshButton")?.addEventListener("click", event => withButton(event.currentTarget, refreshActivePage));
  document.addEventListener("click", event => {
    const button = event.target.closest("[data-action]");
    if (!button) return;
    const actions = {
      connect: () => connectionAction("connect"),
      disconnect: () => connectionAction("disconnect"),
      scan: scanDevice,
      reboot: rebootModem,
      "usb-mode": setUsbMode,
      "nat-mode": setNatMode,
      "save-config": saveConfig,
      "refresh-radio": () => settle([loadBands(), loadCells(), refreshStatus(true)]),
      "save-preference": saveNetworkPreference,
      "refresh-sms": () => loadSms(true),
      "clear-logs": clearLogs
    };
    const action = actions[button.dataset.action];
    if (action) withButton(button, action).catch(showActionError);
  });
  $$('[data-at]').forEach(button => button.addEventListener("click", () => {
    const input = $('#atForm input[name="command"]');
    input.value = button.dataset.at;
    $("#atForm").requestSubmit();
  }));
}

const THEME_KEY = "rm500u-theme";
const THEME_ICONS = { system: "i-monitor", light: "i-sun", dark: "i-moon" };
const THEME_LABELS = { system: "主题：跟随系统", light: "主题：浅色", dark: "主题：深色" };
const THEME_ORDER = ["system", "light", "dark"];

function themePreference() {
  let pref;
  try { pref = localStorage.getItem(THEME_KEY) || "system"; } catch (e) { pref = "system"; }
  return THEME_ICONS[pref] ? pref : "system";
}
function renderThemeToggle(pref) {
  const button = $("#themeToggle");
  if (!button) return;
  const use = button.querySelector("use");
  if (use) use.setAttribute("href", `#${THEME_ICONS[pref] || "i-monitor"}`);
  button.title = THEME_LABELS[pref];
  button.setAttribute("aria-label", THEME_LABELS[pref]);
}
function cycleTheme() {
  const current = themePreference();
  applyThemePreference(THEME_ORDER[(THEME_ORDER.indexOf(current) + 1) % THEME_ORDER.length]);
}
function applyThemePreference(pref) {
  if (window.__rm500uSetTheme) window.__rm500uSetTheme(pref);
  renderThemeToggle(pref);
}
function initTheme() {
  $("#themeToggle")?.addEventListener("click", cycleTheme);
  renderThemeToggle(themePreference());
  window.matchMedia("(prefers-color-scheme: dark)").addEventListener("change", () => {
    if (themePreference() === "system") applyThemePreference("system");
  });
}

function startPolling() {
  clearInterval(state.pollTimer);
  clearInterval(state.smsPollTimer);
  state.pollTimer = setInterval(() => {
    if (document.hidden) return;
    refreshStatus(false, false, true);
  }, 8000);
  state.smsPollTimer = setInterval(() => {
    if (document.hidden || state.activePage !== "sms") return;
    loadSms().catch(() => {});
  }, 30000);
}

async function refreshActivePage() {
  if (state.activePage === "dashboard") return refreshStatus(true, true);
  if (state.activePage === "connection") return settle([loadConfig(), loadCandidates(), loadApn(), refreshStatus(true)]);
  if (state.activePage === "radio") return settle([loadBands(), loadCells(), refreshStatus(true)]);
  if (state.activePage === "sms") return settle([loadSms(true), loadWebhook()]);
  if (state.activePage === "tools") return state.activeTool === "logs" ? loadLogs() : refreshStatus(true);
}

async function refreshStatus(force = false, showBusy = false, silent = false) {
  if (state.statusLoading) return;
  state.statusLoading = true;
  try {
    state.status = await api(`/api/status${force ? "?refresh=true" : ""}`, { track: showBusy });
    renderStatus(state.status);
  } catch (error) {
    renderStatusUnavailable(error);
    if (!silent) throw error;
  } finally {
    state.statusLoading = false;
  }
}

function renderStatus(status) {
  const device = status.device || {};
  const sim = status.sim || {};
  const signal = status.signal || {};
  const connection = status.connection || {};
  const cells = Array.isArray(status.cells) ? status.cells : [];
  const primary = cells.find(cell => String(cell.rat || "").includes("NR5G")) || cells[0] || {};
  const ready = Boolean(status.devicePresent);
  const connected = Boolean(connection.connected);
  const hasSignalData = [signal.rsrp, signal.rsrq, signal.sinr, signal.rssi].some(value => nullableNumber(value) != null);
  const score = ready && hasSignalData ? Math.round(clamp(number(signal.percent), 0, 100)) : null;
  const simulation = status.simulation === true;
  $("#environmentBadge").hidden = !simulation;
  $("#deviceAlert").classList.toggle("is-hidden", ready && !status.error);
  setText("deviceAlertTitle", ready ? "RM500U 状态异常" : "未发现 RM500U");
  setText("deviceAlertText", status.error || "请检查 USB 连接、供电和 AT 端口。");
  setStatusLight($("#sideStatusLight"), ready ? (connected ? "online" : "warning") : "offline");
  setStatusLight($("#heroStatusLight"), ready ? (connected ? "online" : "warning") : "offline");
  setText("sideDeviceState", ready ? (connected ? "设备在线" : "设备就绪") : "设备离线");
  setText("heroLinkLabel", connected ? "数据连接已建立" : ready ? "设备在线，数据未连接" : "设备未响应");
  setText("heroNetworkType", connection.networkType);
  setText("heroOperator", sim.operator, "未注册运营商");
  setText("heroIp", connection.ipAddress);
  setText("heroInterface", connection.interfaceName);
  setText("heroRxRate", formatRate(connection.rxBytesPerSecond));
  setText("heroTxRate", formatRate(connection.txBytesPerSecond));
  setText("heroTrafficTotal", `${formatBytes(connection.rxBytes)} / ${formatBytes(connection.txBytes)}`);
  setText("heroRsrp", formatNumber(signal.rsrp ?? primary.rsrp, " dBm", 0));
  setText("heroRsrq", formatNumber(signal.rsrq ?? primary.rsrq, " dB", 0));
  setText("heroSinr", formatNumber(signal.sinr ?? primary.sinr, " dB", 0));
  $(".signal-gauge").style.setProperty("--signal", `${score ?? 0}%`);
  setText("signalPercent", score);
  setText("signalGrade", score == null ? "--" : signalGrade(score));
  const assessment = assessSignal(score, signal);
  const signalSummary = $("#signalSummary");
  if (signalSummary) signalSummary.className = `signal-summary ${assessment.tone}`;
  setText("signalAssessment", assessment.label);
  setText("signalAssessmentText", assessment.description);
  setText("signalScore", score);
  $("#connectButton").disabled = !ready || connected;
  $("#disconnectButton").disabled = !ready || !connected;
  setText("deviceModel", device.model); setText("deviceVariant", device.variant); setText("deviceFirmware", device.firmware);
  setText("deviceImei", device.imei); setText("deviceUsbMode", device.usbMode); setText("deviceNatMode", device.natMode);
  setText("deviceSimSlot", device.simSlot ? `SIM ${device.simSlot}` : "--");
  setText("deviceTemperature", formatNumber(device.temperatureCelsius, " °C", 1));
  setText("deviceVoltage", formatNumber(device.voltageMillivolts, " mV", 0));
  setText("sideAtPort", device.atPort); setText("sideInterface", connection.interfaceName); setText("atPortLabel", device.atPort ? `PORT ${device.atPort}` : "PORT --");
  const simState = translateSimStatus(sim.status); $("#simStatePill").textContent = simState.label; $("#simStatePill").className = `state-pill ${simState.className}`;
  setText("simOperator", sim.operator); setText("simPhone", sim.phoneNumber); setText("simImsi", sim.imsi); setText("simIccid", sim.iccid);
  setText("networkPreference", connection.networkPreference); setText("interfaceState", connection.interfaceState); setText("totalRx", formatBytes(connection.rxBytes)); setText("totalTx", formatBytes(connection.txBytes));
  renderMetric("Rsrp", signal.rsrp ?? primary.rsrp, -140, -70, -110, -120, "dBm");
  renderMetric("Rsrq", signal.rsrq ?? primary.rsrq, -25, -3, -15, -20, "dB");
  renderMetric("Sinr", signal.sinr ?? primary.sinr, -10, 30, 5, 0, "dB");
  renderMetric("Rssi", signal.rssi ?? primary.rssi, -115, -50, -90, -105, "dBm");
  setText("signalRatPill", connection.networkType || primary.rat);
  renderServingCells(cells); renderQos(status.qos || {}); applyPreferenceChecks(connection.networkPreference); applyDeviceControls(device);
  setText("updatedAt", formatTime(status.updatedAt ? new Date(status.updatedAt) : new Date()));
}

function renderQos(qos) {
  const level = qos.qci != null ? `QCI ${qos.qci}` : qos.fiveQi != null ? `5QI ${qos.fiveQi}` : "--";
  setText("qosLevel", level); setText("qosSource", qos.source ? "设备返回" : "无数据");
  setText("qosDownlink", formatKbps(qos.downlinkSubscribedKbps)); setText("qosUplink", formatKbps(qos.uplinkSubscribedKbps));
  setText("qosGfbrDown", formatKbps(qos.downlinkGuaranteedKbps)); setText("qosGfbrUp", formatKbps(qos.uplinkGuaranteedKbps));
  setText("qosMfbrDown", formatKbps(qos.downlinkMaxKbps)); setText("qosMfbrUp", formatKbps(qos.uplinkMaxKbps));
}

function renderServingCells(cells) {
  setText("servingCellCount", `${cells.length} 个`);
  const body = $("#servingCellsBody");
  if (!cells.length) { body.innerHTML = '<tr class="empty-row"><td colspan="9">暂无服务小区数据</td></tr>'; return; }
  body.innerHTML = cells.map(cell => {
    const prefix = String(cell.rat || "").toUpperCase().includes("NR") ? "N" : "B";
    const bw = cell.downlinkBandwidthMhz || cell.uplinkBandwidthMhz ? `${formatPlain(cell.downlinkBandwidthMhz)} / ${formatPlain(cell.uplinkBandwidthMhz)} MHz` : "--";
    return `<tr><td><span class="state-pill neutral">${escapeHtml(value(cell.rat))}</span></td><td>${escapeHtml(cell.band == null ? "--" : `${prefix}${cell.band}`)}</td><td>${escapeHtml(value(cell.arfcn))}</td><td>${escapeHtml(value(cell.pci))}</td><td>${escapeHtml(value(cell.cellId))}</td><td>${escapeHtml(value(cell.cqi))}</td><td>${escapeHtml(bw)}</td><td>${escapeHtml(formatNumber(cell.rsrp, " dBm", 0))}</td><td>${escapeHtml(formatNumber(cell.sinr, " dB", 0))}</td></tr>`;
  }).join("");
}

function renderMetric(name, raw, minimum, maximum, fair, poor, unit) {
  const parsed = nullableNumber(raw); const output = $(`#metric${name}`); const track = $(`#track${name}`);
  output.textContent = parsed == null ? "--" : `${formatPlain(parsed)} ${unit}`;
  track.style.width = parsed == null ? "0%" : `${clamp((parsed - minimum) / (maximum - minimum) * 100, 0, 100)}%`;
  track.className = parsed == null ? "" : parsed <= poor ? "poor" : parsed <= fair ? "fair" : "";
}

function renderStatusUnavailable(error) {
  setStatusLight($("#sideStatusLight"), "offline"); setStatusLight($("#heroStatusLight"), "offline");
  setText("sideDeviceState", "服务不可用"); setText("heroLinkLabel", "无法读取设备状态"); $("#deviceAlert").classList.remove("is-hidden");
  setText("deviceAlertTitle", "状态读取失败"); setText("deviceAlertText", error.message || "后端服务没有响应。");
}

function applyPreferenceChecks(preference) {
  if (!preference) return;
  const all = String(preference).toUpperCase() === "AUTO";
  $$('[data-page-panel="radio"] input[type="checkbox"]').forEach(input => input.checked = all || String(preference).toUpperCase().split(":").includes(input.value));
  setText("preferenceCurrent", all ? "自动选择" : String(preference).replaceAll(":", " + "));
}

function applyDeviceControls(device) {
  const usb = String(device.usbMode || "").toLowerCase(); if (["ecm", "mbim", "rndis", "ncm"].includes(usb)) $("#usbModeSelect").value = usb;
  const nat = String(device.natMode || "").toLowerCase(); if (["nic", "router", "bridge"].includes(nat)) $("#natModeSelect").value = nat;
  syncCustomSelect($("#usbModeSelect")); syncCustomSelect($("#natModeSelect"));
  $$('[data-slot]').forEach(button => button.classList.toggle("is-active", Number(button.dataset.slot) === Number(device.simSlot)));
}

async function loadConfig() {
  state.config = await api("/api/config");
  const form = $("#configForm"); if (!form) return;
  ["variant", "atPort", "baudRate", "interfaceName", "dhcpClient"].forEach(name => { if (form.elements[name]) form.elements[name].value = state.config[name] ?? ""; });
  ["variant", "baudRate", "dhcpClient"].forEach(name => syncCustomSelect(form.elements[name]));
  form.elements.autoConnect.checked = Boolean(state.config.autoConnect); form.elements.manageInterface.checked = Boolean(state.config.manageInterface);
}

async function saveConfig() {
  const form = $("#configForm");
  const body = { ...state.config, variant: form.elements.variant.value, atPort: form.elements.atPort.value.trim() || "auto", baudRate: Number(form.elements.baudRate.value), interfaceName: form.elements.interfaceName.value.trim() || "auto", dhcpClient: form.elements.dhcpClient.value, autoConnect: form.elements.autoConnect.checked, manageInterface: form.elements.manageInterface.checked };
  state.config = await api("/api/config", { method: "PUT", body });
  toast("设备设置已保存", "拨号参数将在下一次连接时使用。", "success");
  await refreshStatus(true);
}

async function loadCandidates() {
  const candidates = await api("/api/device/candidates", { track: false }); const list = $("#portCandidates"); list.replaceChildren();
  candidates.forEach(candidate => { const option = document.createElement("option"); option.value = candidate.port; option.label = candidate.displayName || candidate.port; list.append(option); });
}

async function scanDevice() { const result = await api("/api/device/scan", { method: "POST" }); toast("发现 RM500U", `${result.port} · ${result.model || ""}`, "success"); await settle([loadCandidates(), refreshStatus(true)]); }
async function connectionAction(action) { const result = await api(`/api/connection/${action}`, { method: "POST" }); notifyResult(result, action === "connect" ? "连接已建立" : "连接已断开"); await refreshStatus(true); }
async function rebootModem() { if (!window.confirm("重启 RM500U 会暂时中断 AT 端口和数据连接，继续吗？")) return; const result = await api("/api/modem/reboot", { method: "POST" }); notifyResult(result, "重启命令已发送"); setTimeout(() => refreshStatus(false, false, true), 1200); }
async function setUsbMode() { const result = await api("/api/modem/usb-mode", { method: "PUT", body: { mode: $("#usbModeSelect").value, reboot: $("#usbReboot").checked } }); notifyResult(result, "USB 模式已更新"); if (!$("#usbReboot").checked) await refreshStatus(true); }
async function setNatMode() { const result = await api("/api/modem/nat-mode", { method: "PUT", body: { mode: $("#natModeSelect").value, reboot: $("#natReboot").checked } }); notifyResult(result, "NAT 模式已更新"); if (!$("#natReboot").checked) await refreshStatus(true); }
async function setSimSlot(event) { const button = event.target.closest("button[data-slot]"); if (!button || button.classList.contains("is-active")) return; const result = await api("/api/modem/sim-slot", { method: "PUT", body: { slot: Number(button.dataset.slot) } }); notifyResult(result, "SIM 卡槽已切换"); await refreshStatus(true); }

async function loadApn() { state.apn = await api("/api/apn"); state.apnSavedSnapshot = snapshotApn(state.apn); renderApn(); }
function snapshotApn(apn) { return JSON.stringify({ profiles: apn.profiles, activeProfileId: apn.activeProfileId }); }
function activeApn() { return state.apn?.profiles?.find(profile => profile.id === state.apn.activeProfileId) || state.apn?.profiles?.[0]; }
function renderApn() {
  if (!state.apn) return;
  const select = $("#apnProfileSelect"); select.replaceChildren();
  (state.apn.profiles || []).forEach(profile => { const option = document.createElement("option"); option.value = profile.id; option.textContent = `${profile.name} (${profile.id})`; option.selected = profile.id === state.apn.activeProfileId; select.append(option); });
  syncCustomSelect(select);
  const profile = activeApn(); if (!profile) return;
  const form = $("#apnForm"); ["name", "id", "apn", "pdpContext", "pdpType", "authentication", "username", "password"].forEach(name => { if (form.elements[name]) form.elements[name].value = profile[name] ?? ""; });
  ["pdpType", "authentication"].forEach(name => syncCustomSelect(form.elements[name]));
  setText("apnSavedState", "已保存"); const modem = state.apn.modemProfile; const same = modem && ["apn", "pdpType", "authentication", "username"].every(key => String(modem[key] ?? "") === String(profile[key] ?? ""));
  setText("apnDeviceState", same ? "设备当前生效" : "设备当前未应用");
  $("#apnDirtyState").hidden = snapshotApn(state.apn) === state.apnSavedSnapshot;
}
function updateApnFromForm() { const form = $("#apnForm"); const id = state.apn?.activeProfileId; if (!id) return; const nextId = form.elements.id.value.trim().toLowerCase(); const updated = { name: form.elements.name.value, id: nextId, apn: form.elements.apn.value.trim(), pdpContext: 1, pdpType: form.elements.pdpType.value, authentication: form.elements.authentication.value, username: form.elements.username.value.trim(), password: form.elements.password.value }; state.apn.profiles = state.apn.profiles.map(profile => profile.id === id ? { ...profile, ...updated } : profile); if (nextId && nextId !== id) state.apn.activeProfileId = nextId; $("#apnDirtyState").hidden = false; }
function selectApnProfile(event) { updateApnFromForm(); state.apn.activeProfileId = event.target.value; renderApn(); }
function newApnProfile() { updateApnFromForm(); const id = `profile-${Date.now().toString(36)}`; state.apn.profiles.push({ id, name: "新 APN", apn: "", pdpContext: 1, pdpType: "IPV4V6", authentication: "none", username: "", password: "" }); state.apn.activeProfileId = id; renderApn(); $("#apnForm input[name=name]").focus(); }
function duplicateApnProfile() { updateApnFromForm(); const current = activeApn(); if (!current) return; const id = `copy-${Date.now().toString(36)}`; state.apn.profiles.push({ ...current, id, name: `${current.name} 副本` }); state.apn.activeProfileId = id; renderApn(); }
function deleteApnProfile() { updateApnFromForm(); if ((state.apn.profiles || []).length <= 1) { toast("至少保留一个档案", "RM500U 需要一个默认 APN 档案。", "warning"); return; } if (!window.confirm("删除当前 APN 档案？")) return; state.apn.profiles = state.apn.profiles.filter(profile => profile.id !== state.apn.activeProfileId); state.apn.activeProfileId = state.apn.profiles[0].id; renderApn(); }
async function saveApnProfiles() { updateApnFromForm(); const result = await api("/api/apn/profiles", { method: "PUT", body: { profiles: state.apn.profiles, activeProfileId: state.apn.activeProfileId } }); state.apn = result; state.apnSavedSnapshot = snapshotApn(result); renderApn(); toast("APN 档案已保存", "配置已写入本地数据目录。", "success"); return result; }
async function applyApn(reconnect) { updateApnFromForm(); if (snapshotApn(state.apn) !== state.apnSavedSnapshot) await saveApnProfiles(); const result = await api("/api/apn/apply", { method: "POST", body: { profileId: state.apn.activeProfileId, reconnect } }); notifyResult(result, reconnect ? "APN 已应用并重新拨号" : "APN 已应用"); if (result.state) { state.apn = result.state; state.apnSavedSnapshot = snapshotApn(result.state); renderApn(); } await refreshStatus(true); }

async function loadBands() { state.bands = await api("/api/radio/bands"); renderBands(); }
function renderBands() { const groups = state.bands?.groups || []; setText("bandVariant", state.bands?.variant); $("#bandGroups").innerHTML = groups.map(group => `<div class="band-group" data-band-rat="${escapeAttribute(group.rat)}"><strong>${escapeHtml(group.rat)}</strong><div class="band-options">${(group.available || []).map(band => `<label><input type="checkbox" value="${Number(band)}" ${(group.selected || []).includes(band) ? "checked" : ""}><span>${escapeHtml(bandName(group.rat, band))}</span></label>`).join("")}</div><button class="button button-compact" data-band-action="save" type="button">应用</button></div>`).join("") || '<div class="empty-state">没有可用频段</div>'; }
async function handleBandAction(event) { const button = event.target.closest("button[data-band-action]"); if (!button) return; const group = button.closest("[data-band-rat]"); const bands = $$('input:checked', group).map(input => Number(input.value)); if (!bands.length) return toast("频段不能为空", "至少选择一个频段。", "warning"); const result = await api("/api/radio/bands", { method: "PUT", body: { rat: group.dataset.bandRat, bands } }); notifyResult(result, "频段已更新"); await loadBands(); }
async function loadCells() { state.cells = await api("/api/cells"); renderCells(); }
function renderCells() { const data = state.cells || {}; const locks = data.locks || []; $("#lockSummary").innerHTML = locks.map(lock => `<button class="lock-chip ${lock.locked ? "locked" : ""}" ${lock.locked ? `data-unlock-rat="${escapeAttribute(lock.rat)}"` : "disabled"}>${escapeHtml(lock.rat)} ${lock.locked ? `${lock.arfcn} / ${lock.pci}` : "未锁定"}</button>`).join(""); const body = $("#neighborCellsBody"); if (!(data.cells || []).length) { body.innerHTML = '<tr class="empty-row"><td colspan="8">没有邻区</td></tr>'; return; } body.innerHTML = data.cells.map(cell => `<tr><td>${escapeHtml(value(cell.rat))}</td><td>${escapeHtml(value(cell.kind))}</td><td>${escapeHtml(value(cell.arfcn))}</td><td>${escapeHtml(value(cell.pci))}</td><td>${escapeHtml(formatNumber(cell.rsrp, " dBm", 0))}</td><td>${escapeHtml(formatNumber(cell.rsrq, " dB", 0))}</td><td>${cell.locked ? "已锁定" : "可用"}</td><td><button class="icon-button subtle" data-lock-cell data-rat="${escapeAttribute(cell.rat)}" data-arfcn="${Number(cell.arfcn)}" data-pci="${Number(cell.pci)}" title="锁定小区" aria-label="锁定小区"><svg><use href="#i-lock"/></svg></button></td></tr>`).join(""); }
async function handleCellAction(event) { const button = event.target.closest("[data-lock-cell]"); if (!button) return; const result = await api("/api/cells/lock", { method: "POST", body: { rat: button.dataset.rat, arfcn: Number(button.dataset.arfcn), pci: Number(button.dataset.pci) } }); notifyResult(result, "小区已锁定"); await settle([loadCells(), refreshStatus(true)]); }
async function submitCellLock(event) { event.preventDefault(); const form = event.currentTarget; const result = await api("/api/cells/lock", { method: "POST", body: { rat: form.elements.rat.value, arfcn: Number(form.elements.arfcn.value), pci: Number(form.elements.pci.value) } }); notifyResult(result, "小区已锁定"); await settle([loadCells(), refreshStatus(true)]); }
async function handleUnlockAction(event) { const button = event.target.closest("[data-unlock-rat]"); if (!button) return; const result = await api("/api/cells/unlock", { method: "POST", body: { rat: button.dataset.unlockRat } }); notifyResult(result, "小区锁定已解除"); await loadCells(); }
async function saveNetworkPreference() { const modes = $$('#networkModeSelector input:checked').map(input => input.value); if (!modes.length) return toast("未选择制式", "至少保留一个网络制式。", "warning"); const result = await api("/api/radio/preference", { method: "PUT", body: { modes } }); notifyResult(result, "网络偏好已更新"); await refreshStatus(true); }

async function loadSms(forceRefresh = false) {
  if (state.smsLoadTask) return state.smsLoadTask;

  const loadTask = api(`/api/sms${forceRefresh ? "?refresh=true" : ""}`).then(result => {
    state.sms = result;
    renderSms();
    if (result?.isRefreshing) scheduleSmsRefresh();
    else if (state.smsRefreshTimer) {
      clearTimeout(state.smsRefreshTimer);
      state.smsRefreshTimer = null;
    }
    return result;
  });
  state.smsLoadTask = loadTask;
  try {
    return await loadTask;
  } finally {
    if (state.smsLoadTask === loadTask) state.smsLoadTask = null;
  }
}
function scheduleSmsRefresh() { clearTimeout(state.smsRefreshTimer); state.smsRefreshTimer = setTimeout(() => { state.smsRefreshTimer = null; if (!document.hidden && state.activePage === "sms") loadSms().catch(() => {}); }, 1200); }
function renderSms() { const conversations = state.sms?.conversations || []; const unread = conversations.reduce((sum, conversation) => sum + Number(conversation.unreadCount || 0), 0); $("#smsNavCount").hidden = unread === 0; setText("smsNavCount", unread); setText("smsStorage", state.sms?.storage); setText("smsUsage", `${state.sms?.used ?? 0} / ${state.sms?.total ?? 0}`); const list = $("#conversationList"); if (!conversations.length) { list.innerHTML = `<div class="empty-state">${state.sms?.isRefreshing ? "正在读取短信..." : "SIM 卡中没有短信"}</div>`; $("#threadMessages").innerHTML = '<div class="empty-state">没有可显示的会话</div>'; return; } if (!conversations.some(conversation => conversation.peer === state.selectedPeer)) state.selectedPeer = conversations[0].peer; list.innerHTML = conversations.map(conversation => { const last = conversation.messages?.at(-1); return `<button type="button" class="conversation-item ${conversation.peer === state.selectedPeer ? "is-active" : ""}" data-peer="${escapeAttribute(conversation.peer)}"><strong>${escapeHtml(conversation.peer)}</strong><span>${escapeHtml(last?.content || "（空短信）")}</span><time>${escapeHtml(formatDateTime(conversation.lastTimestamp))}</time>${conversation.unreadCount ? `<b>${conversation.unreadCount}</b>` : ""}</button>`; }).join(""); const current = conversations.find(conversation => conversation.peer === state.selectedPeer); renderThread(current); }
function renderThread(conversation) { setText("threadTitle", conversation?.peer || "选择一个号码"); const container = $("#threadMessages"); if (!conversation) { container.innerHTML = '<div class="empty-state">从左侧选择会话</div>'; return; } container.innerHTML = conversation.messages.map(message => `<article class="message-bubble ${String(message.direction).toLowerCase() === "outgoing" ? "outgoing" : "incoming"}"><div class="bubble-meta"><time>${escapeHtml(formatDateTime(message.timestamp, true))}</time><span>${message.segmentCount > 1 ? `${message.segmentCount} 段` : ""}</span><button type="button" data-sms-delete="${message.index}" title="删除消息" aria-label="删除消息"><svg><use href="#i-trash"/></svg></button></div><p>${escapeHtml(message.content || "（空短信）")}</p></article>`).join(""); $("#smsReplyForm").elements.recipient.value = conversation.peer; const unread = conversation.messages.filter(message => message.unread).flatMap(message => message.indices || [message.index]); if (unread.length) api("/api/sms/read", { method: "POST", body: { indices: unread } }).then(() => { markSmsReadLocally(unread); renderSms(); }).catch(() => {}); }
function markSmsReadLocally(indices) { const readIndexes = new Set(indices.map(Number)); for (const conversation of state.sms?.conversations || []) { for (const message of conversation.messages || []) { const messageIndexes = message.indices || [message.index]; if (message.unread && messageIndexes.some(index => readIndexes.has(Number(index)))) message.unread = false; } conversation.unreadCount = (conversation.messages || []).filter(message => message.unread).length; } }
function openConversation(peer) { state.selectedPeer = peer; renderSms(); }
function newSms() { state.selectedPeer = ""; $("#smsForm input[name=recipient]").focus(); }
async function sendSms(event) { event.preventDefault(); const form = event.currentTarget; const result = await api("/api/sms", { method: "POST", body: { recipient: form.elements.recipient.value.trim(), content: form.elements.content.value } }); notifyResult(result, "短信已发送"); if (result.success) { form.reset(); setText("smsCharCount", "0 / 500"); await loadSms(); } }
async function sendReply(event) { event.preventDefault(); const form = event.currentTarget; if (!form.elements.recipient.value) return toast("请先选择会话", "", "warning"); const result = await api("/api/sms", { method: "POST", body: { recipient: form.elements.recipient.value, content: form.elements.content.value } }); notifyResult(result, "回复已发送"); if (result.success) { form.elements.content.value = ""; setText("replyCount", "0 / 500"); await loadSms(); } }
async function deleteSmsMessage(button) { const message = findSmsMessage(Number(button.dataset.smsDelete)); if (!message || !window.confirm("删除这条短信的全部分段？")) return; const result = await api("/api/sms/delete", { method: "POST", body: { indices: message.indices || [message.index] } }); notifyResult(result, "短信已删除"); await loadSms(); }
function findSmsMessage(index) { return (state.sms?.conversations || []).flatMap(conversation => conversation.messages || []).find(message => Number(message.index) === index); }

async function loadWebhook() { state.webhook = await api("/api/sms/webhook"); renderWebhook(); }
function renderWebhook() { const form = $("#webhookForm"); if (!state.webhook || !form) return; form.elements.enabled.checked = Boolean(state.webhook.enabled); form.elements.url.value = state.webhook.url || ""; setText("webhookState", state.webhook.enabled ? "已启用" : "已关闭"); $("#webhookState").className = `state-pill ${state.webhook.enabled ? "success" : "neutral"}`; setText("webhookLast", state.webhook.lastAttemptAt ? `最近投递：${formatDateTime(state.webhook.lastAttemptAt, true)} · ${state.webhook.lastSuccess ? "成功" : state.webhook.lastError || "失败"}` : "最近投递：--"); }
async function saveWebhook() { const form = $("#webhookForm"); const body = { enabled: form.elements.enabled.checked, url: form.elements.url.value.trim(), secret: form.elements.secret.value, timeoutSeconds: Number(form.elements.timeoutSeconds.value) || 10, maxRetries: Number(form.elements.maxRetries.value) || 0 }; state.webhook = await api("/api/sms/webhook", { method: "PUT", body }); renderWebhook(); toast("webhook 设置已保存", "启用后首次轮询只建立基线，不补发历史短信。", "success"); }
async function testWebhook() { const result = await api("/api/sms/webhook/test", { method: "POST" }); notifyResult(result, "webhook 测试完成"); await loadWebhook(); }

async function sendAtCommand(event) { event.preventDefault(); const form = event.currentTarget; const command = form.elements.command.value.trim(); if (!command) return; appendTerminal("TX", command, "command"); const result = await api("/api/at", { method: "POST", body: { command, timeoutSeconds: Number(form.elements.timeoutSeconds.value) || 8 } }); appendTerminal(result.timedOut ? "TIMEOUT" : result.success ? "RX" : "ERROR", result.raw || "（无返回）", result.success ? "response" : "error", `${result.elapsedMilliseconds || 0} ms`); }
function appendTerminal(tag, text, type = "response", elapsed = "") { const output = $("#terminalOutput"); const entry = document.createElement("div"); entry.className = `terminal-entry ${type}`; entry.innerHTML = `<span>${escapeHtml(tag)}</span><pre>${escapeHtml(text)}</pre><time>${escapeHtml(elapsed || formatTime(new Date()))}</time>`; output.append(entry); while (output.children.length > 80) output.firstElementChild.remove(); output.scrollTop = output.scrollHeight; }
async function loadLogs() { state.logs = await api("/api/logs"); renderLogs(); }
function renderLogs() { const filter = $("#logLevelFilter")?.value || "all"; const logs = state.logs.filter(entry => filter === "all" || normalizeLogLevel(entry.level) === filter); $("#logsBody").innerHTML = logs.length ? logs.map(entry => `<tr><td>${escapeHtml(formatDateTime(entry.timestamp, true))}</td><td><span class="log-level ${normalizeLogLevel(entry.level)}">${escapeHtml(normalizeLogLevel(entry.level))}</span></td><td>${escapeHtml(value(entry.source))}</td><td>${escapeHtml(value(entry.message))}</td><td>${escapeHtml(value(entry.detail))}</td></tr>`).join("") : '<tr class="empty-row"><td colspan="5">没有日志</td></tr>'; }
async function clearLogs() { if (!window.confirm("清空运行日志？")) return; await api("/api/logs", { method: "DELETE" }); state.logs = []; renderLogs(); toast("日志已清空", "", "success"); }

async function api(path, options = {}) { const method = options.method || "GET"; if (options.track !== false) setBusy(true); try { const response = await fetch(path, { method, headers: options.body == null ? undefined : { "Content-Type": "application/json" }, body: options.body == null ? undefined : JSON.stringify(options.body), credentials: "same-origin", cache: method === "GET" ? "no-store" : "default" }); const text = await response.text(); let payload = null; try { payload = text ? JSON.parse(text) : null; } catch { payload = text; } if (!response.ok) throw new Error(typeof payload === "object" && payload ? payload.message || payload.detail || `请求失败 (${response.status})` : payload || `请求失败 (${response.status})`); return payload; } catch (error) { if (error instanceof TypeError) throw new Error("无法连接 RM500U 管理服务"); throw error; } finally { if (options.track !== false) setBusy(false); } }
function setBusy(active) { state.pendingRequests = Math.max(0, state.pendingRequests + (active ? 1 : -1)); document.body.classList.toggle("is-busy", state.pendingRequests > 0); }
async function withButton(button, task) { if (!button || button.dataset.busy === "true") return; button.dataset.busy = "true"; button.disabled = true; try { return await task(); } finally { button.dataset.busy = "false"; button.disabled = false; } }
function notifyResult(result, fallback) { toast(result?.success === false ? "操作未完成" : fallback, result?.message || "", result?.success === false ? "error" : "success"); }
function toast(title, message = "", type = "success") { const region = $("#toastRegion"); const item = document.createElement("div"); item.className = `toast ${type}`; item.innerHTML = `<strong>${escapeHtml(title)}</strong><span>${escapeHtml(message)}</span>`; region.append(item); setTimeout(() => item.remove(), type === "error" ? 7000 : 4200); }
function showActionError(error) { toast("操作失败", error.message || "设备没有完成请求。", "error"); }
function showLoadError(error) { toast("数据读取失败", error.message || "暂时无法读取设备数据。", "error"); }
async function settle(tasks) { const results = await Promise.allSettled(tasks); const failed = results.find(result => result.status === "rejected"); if (failed) showLoadError(failed.reason); return results; }
function setStatusLight(element, status) { if (!element) return; element.classList.remove("online", "warning"); if (status === "online") element.classList.add("online"); if (status === "warning") element.classList.add("warning"); }
function setText(id, raw, fallback = "--") { const element = document.getElementById(id); if (element) element.textContent = value(raw, fallback); }
function value(raw, fallback = "--") { return raw === null || raw === undefined || raw === "" ? fallback : String(raw); }
function number(raw, fallback = 0) { const parsed = Number(raw); return Number.isFinite(parsed) ? parsed : fallback; }
function nullableNumber(raw) { const parsed = Number(raw); return raw === null || raw === undefined || raw === "" || !Number.isFinite(parsed) ? null : parsed; }
function clamp(raw, min, max) { return Math.min(max, Math.max(min, raw)); }
function formatPlain(raw) { const parsed = nullableNumber(raw); return parsed == null ? "--" : Number.isInteger(parsed) ? String(parsed) : parsed.toFixed(1).replace(/\.0$/, ""); }
function formatNumber(raw, suffix = "", digits = 0) { const parsed = nullableNumber(raw); return parsed == null ? "--" : `${parsed.toFixed(digits)}${suffix}`; }
function formatBytes(raw) { let bytes = number(raw); const units = ["B", "KiB", "MiB", "GiB"]; let index = 0; while (Math.abs(bytes) >= 1024 && index < units.length - 1) { bytes /= 1024; index++; } return `${bytes.toFixed(index ? 1 : 0)} ${units[index]}`; }
function formatRate(raw) { return `${formatBytes(raw)}/s`; }
function formatKbps(raw) { const kbps = nullableNumber(raw); if (kbps == null) return "--"; if (kbps >= 1000) return `${formatPlain(kbps / 1000)} Mbps`; return `${formatPlain(kbps)} Kbps`; }
function formatTime(raw) { const date = raw instanceof Date ? raw : new Date(raw); return Number.isNaN(date.getTime()) ? "--:--:--" : new Intl.DateTimeFormat("zh-CN", { hour: "2-digit", minute: "2-digit", second: "2-digit", hour12: false }).format(date); }
function formatDateTime(raw, seconds = false) { if (!raw) return "--"; const date = new Date(raw); return Number.isNaN(date.getTime()) ? value(raw) : new Intl.DateTimeFormat("zh-CN", { month: "2-digit", day: "2-digit", hour: "2-digit", minute: "2-digit", ...(seconds ? { second: "2-digit" } : {}), hour12: false }).format(date); }
function assessSignal(percent, signal) {
  const rsrp = nullableNumber(signal.rsrp);
  const rsrq = nullableNumber(signal.rsrq);
  const sinr = nullableNumber(signal.sinr);
  if (rsrp == null && rsrq == null && sinr == null) return { label: "--", description: "等待射频数据", tone: "" };
  if (sinr != null && sinr <= 0) return { label: "需要关注", description: "SINR 偏低，当前吞吐可能受干扰影响。", tone: "is-poor" };
  if (rsrq != null && rsrq <= -15) return { label: "质量偏弱", description: "RSRQ 偏低，可能存在干扰或小区负载。", tone: "is-warning" };
  if (rsrp != null && rsrp <= -110) return { label: "覆盖偏弱", description: "RSRP 偏低，建议检查天线位置和周边环境。", tone: "is-warning" };
  if (sinr != null && sinr <= 5) return { label: "基本可用", description: "链路已建立，但 SINR 仍有优化空间。", tone: "is-warning" };
  const grade = signalGrade(percent);
  return { label: grade, description: grade === "优秀" ? "覆盖与无线质量处于良好范围。" : "链路可用，建议继续观察指标变化。", tone: percent >= 60 ? "is-good" : "is-warning" };
}
function signalGrade(percent) { return percent >= 80 ? "优秀" : percent >= 60 ? "良好" : percent >= 35 ? "一般" : percent > 0 ? "较弱" : "--"; }
function translateSimStatus(status) { const normalized = String(status || "").toUpperCase(); if (normalized === "READY") return { label: "已就绪", className: "success" }; if (normalized.includes("PIN") || normalized.includes("PUK")) return { label: "需要解锁", className: "danger" }; if (normalized.includes("NOT") || normalized.includes("ABSENT")) return { label: "未插卡", className: "danger" }; return { label: value(status, "未知"), className: "neutral" }; }
function bandName(rat, band) { return String(rat).toUpperCase().startsWith("NR") ? `n${band}` : `B${band}`; }
function normalizeLogLevel(level) { const normalized = String(level || "info").toLowerCase(); return ["warn", "warning"].includes(normalized) ? "warning" : ["error", "critical", "fatal"].includes(normalized) ? "error" : "info"; }
function escapeHtml(raw) { return value(raw).replace(/[&<>"']/g, character => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[character]); }
function escapeAttribute(raw) { return escapeHtml(raw); }
