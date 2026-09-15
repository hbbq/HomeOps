const devicesElement = document.querySelector("#devices");
const statusElement = document.querySelector("#status");
const refreshButton = document.querySelector("#refresh");
const deviceTemplate = document.querySelector("#device-template");
const measurementTemplate = document.querySelector("#measurement-template");

const numberFormatter = new Intl.NumberFormat(undefined, { maximumFractionDigits: 2 });
const dateFormatter = new Intl.DateTimeFormat(undefined, {
  dateStyle: "medium",
  timeStyle: "medium"
});

let isLoading = false;
let reloadRequested = false;

function formatValue(measurement) {
  if (measurement.kind.toLowerCase() === "boolean") {
    return Number(measurement.value) === 0 ? "No" : "Yes";
  }

  const value = numberFormatter.format(Number(measurement.value));
  return measurement.unit ? `${value} ${measurement.unit}` : value;
}

function render(devices, measurements) {
  devicesElement.replaceChildren();

  if (devices.length === 0) {
    statusElement.textContent = "No devices have been discovered yet.";
    return;
  }

  const groups = new Map();
  for (const measurement of measurements) {
    const group = groups.get(measurement.deviceId) ?? [];
    group.push(measurement);
    groups.set(measurement.deviceId, group);
  }

  for (const deviceDetails of devices) {
    const deviceFragment = deviceTemplate.content.cloneNode(true);
    const deviceElement = deviceFragment.querySelector(".device");
    deviceElement.classList.toggle("disabled", !deviceDetails.isEnabled);
    deviceFragment.querySelector("h2").textContent = deviceDetails.name;
    deviceFragment.querySelector(".device-source").textContent = deviceDetails.source;

    const toggle = deviceFragment.querySelector(".device-toggle");
    toggle.dataset.deviceId = deviceDetails.id;
    toggle.dataset.enabled = deviceDetails.isEnabled;
    toggle.textContent = deviceDetails.isEnabled ? "Disable" : "Enable";
    toggle.classList.toggle("secondary", deviceDetails.isEnabled);

    const measurementList = deviceFragment.querySelector(".measurements");
    const deviceMeasurements = groups.get(deviceDetails.id) ?? [];

    if (!deviceDetails.isEnabled) {
      measurementList.classList.add("empty");
      measurementList.textContent = "Disabled devices are hidden from the API.";
    } else if (deviceMeasurements.length === 0) {
      measurementList.classList.add("empty");
      measurementList.textContent = "No measurements have been recorded yet.";
    }

    for (const measurement of deviceMeasurements) {
      const item = measurementTemplate.content.cloneNode(true);
      item.querySelector(".point-name").textContent = measurement.pointName;
      item.querySelector(".value").textContent = formatValue(measurement);

      const timestamp = new Date(measurement.timestamp);
      const timeElement = item.querySelector("time");
      timeElement.dateTime = timestamp.toISOString();
      timeElement.textContent = dateFormatter.format(timestamp);
      measurementList.append(item);
    }

    devicesElement.append(deviceFragment);
  }

  statusElement.textContent = `Updated ${dateFormatter.format(new Date())}`;
}

async function loadMeasurements() {
  if (isLoading) {
    reloadRequested = true;
    return;
  }

  isLoading = true;
  refreshButton.disabled = true;
  statusElement.classList.remove("error");

  try {
    const [devicesResponse, measurementsResponse] = await Promise.all([
      fetch("/api/dashboard/devices", { cache: "no-store" }),
      fetch("/api/measurements/latest", { cache: "no-store" })
    ]);
    if (!devicesResponse.ok || !measurementsResponse.ok) {
      throw new Error(`Requests failed with statuses ${devicesResponse.status} and ${measurementsResponse.status}`);
    }

    render(await devicesResponse.json(), await measurementsResponse.json());
  } catch (error) {
    console.error(error);
    statusElement.textContent = "Could not load measurements. Try refreshing again.";
    statusElement.classList.add("error");
  } finally {
    isLoading = false;
    refreshButton.disabled = false;

    if (reloadRequested) {
      reloadRequested = false;
      loadMeasurements();
    }
  }
}

async function setDeviceEnabled(button) {
  const enabled = button.dataset.enabled !== "true";
  button.disabled = true;
  statusElement.classList.remove("error");

  try {
    const response = await fetch(`/api/dashboard/devices/${button.dataset.deviceId}/enabled`, {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ enabled })
    });
    if (!response.ok) {
      throw new Error(`Request failed with status ${response.status}`);
    }

    await loadMeasurements();
  } catch (error) {
    console.error(error);
    statusElement.textContent = "Could not update the device. Try again.";
    statusElement.classList.add("error");
    button.disabled = false;
  }
}

refreshButton.addEventListener("click", loadMeasurements);
devicesElement.addEventListener("click", event => {
  const button = event.target.closest(".device-toggle");
  if (button) {
    setDeviceEnabled(button);
  }
});
loadMeasurements();
setInterval(loadMeasurements, 30_000);
