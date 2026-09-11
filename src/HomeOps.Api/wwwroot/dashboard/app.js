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

function formatValue(measurement) {
  if (measurement.kind.toLowerCase() === "boolean") {
    return Number(measurement.value) === 0 ? "No" : "Yes";
  }

  const value = numberFormatter.format(Number(measurement.value));
  return measurement.unit ? `${value} ${measurement.unit}` : value;
}

function render(measurements) {
  devicesElement.replaceChildren();

  if (measurements.length === 0) {
    statusElement.textContent = "No measurements have been recorded yet.";
    return;
  }

  const groups = new Map();
  for (const measurement of measurements) {
    const group = groups.get(measurement.deviceName) ?? [];
    group.push(measurement);
    groups.set(measurement.deviceName, group);
  }

  for (const [deviceName, deviceMeasurements] of groups) {
    const device = deviceTemplate.content.cloneNode(true);
    device.querySelector("h2").textContent = deviceName;
    const measurementList = device.querySelector(".measurements");

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

    devicesElement.append(device);
  }

  statusElement.textContent = `Updated ${dateFormatter.format(new Date())}`;
}

async function loadMeasurements() {
  refreshButton.disabled = true;
  statusElement.classList.remove("error");

  try {
    const response = await fetch("/api/measurements/latest", { cache: "no-store" });
    if (!response.ok) {
      throw new Error(`Request failed with status ${response.status}`);
    }

    render(await response.json());
  } catch (error) {
    console.error(error);
    statusElement.textContent = "Could not load measurements. Try refreshing again.";
    statusElement.classList.add("error");
  } finally {
    refreshButton.disabled = false;
  }
}

refreshButton.addEventListener("click", loadMeasurements);
loadMeasurements();
setInterval(loadMeasurements, 30_000);
