(() => {
  const charts = [];

  const parseJson = id => {
    const node = document.getElementById(id);
    if (!node) return null;
    try { return JSON.parse(node.textContent); } catch { return null; }
  };

  const money = value => value == null || Number.isNaN(Number(value))
    ? "Unavailable"
    : new Intl.NumberFormat("en-GB", { style: "currency", currency: "GBP", minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(Number(value));

  const number = (value, digits = 2) => new Intl.NumberFormat("en-GB", {
    minimumFractionDigits: digits,
    maximumFractionDigits: digits
  }).format(Number(value || 0));

  const dateOnly = value => new Date(value + "T00:00:00Z");

  const labelFor = (iso, granularity) => {
    const d = dateOnly(iso);
    if (granularity === "year") return String(d.getUTCFullYear());
    if (granularity === "month") return d.toLocaleDateString("en-GB", { month: "short", year: "numeric", timeZone: "UTC" });
    return d.toLocaleDateString("en-GB", { day: "numeric", month: "short", year: "numeric", timeZone: "UTC" });
  };

  const aggregateYears = monthly => {
    const groups = new Map();
    monthly.forEach(row => {
      const year = row.date.slice(0, 4);
      if (!groups.has(year)) groups.set(year, []);
      groups.get(year).push(row);
    });
    const sum = (rows, key) => rows.reduce((total, row) => total + Number(row[key] || 0), 0);
    return Array.from(groups.entries()).sort(([a], [b]) => a.localeCompare(b)).map(([year, rows]) => ({
      date: year + "-01-01",
      importKwh: sum(rows, "importKwh"),
      exportKwh: sum(rows, "exportKwh"),
      gasKwh: sum(rows, "gasKwh"),
      importCost: sum(rows, "importCost"),
      exportIncome: sum(rows, "exportIncome"),
      gasCost: sum(rows, "gasCost"),
      standingCharge: sum(rows, "standingCharge"),
      standingExact: rows.every(x => x.standingExact !== false),
      importExact: rows.every(x => !Number(x.importKwh) || x.importExact),
      exportExact: rows.every(x => !Number(x.exportKwh) || x.exportExact),
      gasExact: rows.every(x => !Number(x.gasKwh) || x.gasExact),
      peakKwh: sum(rows, "peakKwh"),
      offPeakKwh: sum(rows, "offPeakKwh"),
      unknownKwh: sum(rows, "unknownKwh"),
      peakCost: sum(rows, "peakCost"),
      offPeakCost: sum(rows, "offPeakCost"),
      peakExact: rows.every(x => !Number(x.peakKwh) || x.peakExact),
      offPeakExact: rows.every(x => !Number(x.offPeakKwh) || x.offPeakExact)
    }));
  };

  const filterRange = (rows, range) => {
    if (!rows.length || range === "all") return rows;
    const last = dateOnly(rows[rows.length - 1].date);
    const from = new Date(last);
    if (range === "30d") from.setUTCDate(from.getUTCDate() - 29);
    if (range === "3m") from.setUTCMonth(from.getUTCMonth() - 3);
    if (range === "1y") from.setUTCFullYear(from.getUTCFullYear() - 1);
    return rows.filter(row => dateOnly(row.date) >= from);
  };

  const chartBase = () => ({
    backgroundColor: "transparent",
    animationDuration: 250,
    textStyle: { color: "#aeb9c8", fontFamily: "Inter,system-ui,sans-serif" },
    grid: { left: 58, right: 24, top: 54, bottom: 72, containLabel: false },
    legend: {
      top: 8,
      left: 8,
      textStyle: { color: "#aeb9c8" },
      inactiveColor: "#4d5a6c",
      selectedMode: true
    },
    tooltip: {
      trigger: "axis",
      backgroundColor: "#0b111a",
      borderColor: "#33445c",
      textStyle: { color: "#edf2f8" },
      axisPointer: { type: "cross", lineStyle: { color: "#58708f" } }
    },
    dataZoom: [
      { type: "inside", filterMode: "filter", zoomOnMouseWheel: true, moveOnMouseMove: true, moveOnMouseWheel: false },
      { type: "slider", filterMode: "filter", height: 22, bottom: 18, borderColor: "#223044", backgroundColor: "#0b111a", fillerColor: "#755fff33", handleStyle: { color: "#8c72ff" }, textStyle: { color: "#8290a3" } }
    ],
    xAxis: {
      type: "category",
      boundaryGap: false,
      axisLine: { lineStyle: { color: "#34455e" } },
      axisLabel: { color: "#8290a3", hideOverlap: true },
      axisTick: { show: false }
    },
    yAxis: {
      type: "value",
      splitLine: { lineStyle: { color: "#223044" } },
      axisLabel: { color: "#8290a3" }
    }
  });

  const initOverview = () => {
    const data = parseJson("overview-explorer-data");
    const root = document.getElementById("overview-explorer");
    if (!data || !root || !window.echarts) return;

    const chart = echarts.init(root);
    const billRoot = document.getElementById("total-bill-chart");
    const billChart = billRoot ? echarts.init(billRoot) : null;
    charts.push(chart);
    if (billChart) charts.push(billChart);
    let granularity = "month";
    let metric = "energy";
    let range = "1y";
    let includeStanding = true;

    const periodButtons = Array.from(document.querySelectorAll("[data-period]"));
    const metricButtons = Array.from(document.querySelectorAll("[data-metric]"));
    const rangeButtons = Array.from(document.querySelectorAll("[data-range]"));

    const setActive = (buttons, key, attr) => buttons.forEach(btn => btn.classList.toggle("active", btn.dataset[attr] === key));

    const updateCards = () => {
      const p = data.periods[granularity];
      if (!p) return;
      document.getElementById("metric-period-label").textContent = (granularity === "day" ? "TODAY" : granularity.toUpperCase()) + " COMBINED UTILITY";
      document.getElementById("metric-combined").textContent = p.combinedExact ? money(p.combinedCost) : "Unavailable";
      document.getElementById("metric-combined-sub").textContent = p.label;
      document.getElementById("metric-electricity").textContent = number(p.importKwh) + " kWh";
      document.getElementById("metric-electricity-sub").textContent = p.electricityExact ? money(p.electricityNetCost) + " net" : "Cost unavailable";
      document.getElementById("metric-export").textContent = number(p.exportKwh) + " kWh";
      document.getElementById("metric-export-sub").textContent = p.exportExact ? money(p.exportIncome) + " income" : "Income unavailable";
      document.getElementById("metric-gas").textContent = number(p.gasKwh) + " kWh";
      document.getElementById("metric-gas-sub").textContent = !p.gasExact ? "Usage cost unavailable" : p.standingExact ? money(p.gasUsageCost) + " usage · " + money(p.standingCharge) + " standing" : money(p.gasUsageCost) + " usage · standing unavailable";
    };

    const rowsFor = () => {
      const source = granularity === "day" ? data.daily : granularity === "month" ? data.monthly : aggregateYears(data.monthly);
      return filterRange(source, range);
    };

    const eventMarks = labels => {
      const visible = new Set(labels);
      const seen = new Set();
      return (data.events || []).map(event => ({
        event,
        axis: labelFor(event.date, granularity)
      })).filter(item => visible.has(item.axis)).filter(item => {
        const key = item.axis + "|" + item.event.label;
        if (seen.has(key)) return false;
        seen.add(key);
        return true;
      }).map(item => ({
        name: item.event.label,
        xAxis: item.axis,
        lineStyle: { color: "#ffcf70", type: "dashed", width: 1 },
        label: {
          show: true,
          formatter: item.event.label,
          color: "#ffcf70",
          backgroundColor: "#0b111acc",
          borderRadius: 4,
          padding: [3, 5],
          position: "insideEndTop"
        }
      }));
    };

    const addEventMarkers = (option, labels) => {
      const marks = eventMarks(labels);
      if (!marks.length || !option.series?.length) return;
      option.series[0].markLine = {
        symbol: ["none", "none"],
        silent: false,
        data: marks
      };
    };

    const line = (name, values, color, unit) => ({
      name,
      type: "line",
      showSymbol: values.length < 45,
      symbolSize: 6,
      smooth: false,
      connectNulls: false,
      lineStyle: { width: 2, color },
      itemStyle: { color },
      emphasis: { focus: "series" },
      data: values,
      tooltip: { valueFormatter: v => unit === "£" ? money(v) : number(v) + " kWh" }
    });

    const render = () => {
      const rows = rowsFor();
      const labels = rows.map(x => labelFor(x.date, granularity));
      const option = chartBase();
      option.xAxis.data = labels;

      if (metric === "energy") {
        document.getElementById("explorer-title").textContent = "Energy flow";
        document.getElementById("explorer-description").textContent = "Electricity import/export, gas usage and net grid usage on one timeline. Home events appear as dashed markers.";
        option.yAxis.axisLabel.formatter = value => number(value, Math.abs(value) < 20 ? 1 : 0);
        option.series = [
          line("Import", rows.map(x => Number(x.importKwh)), "#57b8ff", "kWh"),
          line("Export", rows.map(x => Number(x.exportKwh)), "#37d7a0", "kWh"),
          line("Gas", rows.map(x => Number(x.gasKwh)), "#ffb454", "kWh"),
          line("Net grid", rows.map(x => Number(x.importKwh) - Number(x.exportKwh)), "#9d7bff", "kWh")
        ];
      } else if (metric === "cost") {
        document.getElementById("explorer-title").textContent = "Energy cost";
        document.getElementById("explorer-description").textContent = "Usage costs only. Standing charges are separated into the Total bill graph below; export income is shown below zero.";
        option.yAxis.axisLabel.formatter = value => "£" + number(value, Math.abs(value) < 20 ? 1 : 0);
        option.series = [
          line("Import cost", rows.map(x => x.importExact ? Number(x.importCost) : null), "#ff6b8a", "£"),
          line("Export income", rows.map(x => x.exportExact ? -Number(x.exportIncome) : null), "#37d7a0", "£"),
          line("Gas usage cost", rows.map(x => x.gasExact ? Number(x.gasCost) : null), "#ffb454", "£"),
          line("Combined usage", rows.map(x => (x.importExact && (!Number(x.exportKwh) || x.exportExact) && (!Number(x.gasKwh) || x.gasExact)) ? Number(x.importCost) - Number(x.exportIncome) + Number(x.gasCost) : null), "#9d7bff", "£")
        ];
      } else {
        document.getElementById("explorer-title").textContent = "Tariff mix";
        document.getElementById("explorer-description").textContent = "Peak, off-peak and any unclassified electricity import from the trusted rollups.";
        option.xAxis.boundaryGap = true;
        option.yAxis.axisLabel.formatter = value => number(value, Math.abs(value) < 20 ? 1 : 0);
        option.series = [
          { name: "Peak", type: "bar", stack: "mix", data: rows.map(x => Number(x.peakKwh)), itemStyle: { color: "#ff6b8a" }, tooltip: { valueFormatter: v => number(v) + " kWh" } },
          { name: "Off-peak", type: "bar", stack: "mix", data: rows.map(x => Number(x.offPeakKwh)), itemStyle: { color: "#57b8ff" }, tooltip: { valueFormatter: v => number(v) + " kWh" } },
          { name: "Unclassified", type: "bar", stack: "mix", data: rows.map(x => Number(x.unknownKwh)), itemStyle: { color: "#ffcf70" }, tooltip: { valueFormatter: v => number(v) + " kWh" } }
        ];
      }
      addEventMarkers(option, labels);
      chart.setOption(option, true);
    };

    const renderSupplementary = () => {
      const rows = rowsFor();
      const labels = rows.map(x => labelFor(x.date, granularity));

      if (billChart) {
        const option = chartBase();
        option.xAxis.data = labels;
        option.xAxis.boundaryGap = true;
        option.yAxis.axisLabel.formatter = value => "£" + number(value, Math.abs(value) < 20 ? 1 : 0);

        const standing = rows.map(x => includeStanding ? Number(x.standingCharge || 0) : 0);
        const gasUsage = rows.map(x => x.gasExact ? Number(x.gasCost) : null);
        const gross = rows.map((x, i) => {
          const gasIsExact = !Number(x.gasKwh) || x.gasExact;
          const standingIsExact = !includeStanding || x.standingExact !== false;
          return x.importExact && gasIsExact && standingIsExact ? Number(x.importCost) + Number(gasUsage[i] || 0) + standing[i] : null;
        });
        const net = rows.map((x, i) => {
          const exportIsExact = !Number(x.exportKwh) || x.exportExact;
          return gross[i] != null && exportIsExact ? Number(gross[i]) - Number(x.exportIncome || 0) : null;
        });

        option.series = [
          { name: "Electricity usage", type: "bar", stack: "bill", data: rows.map(x => x.importExact ? Number(x.importCost) : null), itemStyle: { color: "#57b8ff" }, tooltip: { valueFormatter: money } },
          { name: "Gas usage", type: "bar", stack: "bill", data: gasUsage, itemStyle: { color: "#ffb454" }, tooltip: { valueFormatter: money } },
          ...(includeStanding ? [{ name: "Standing charges", type: "bar", stack: "bill", data: rows.map((x, i) => x.standingExact === false ? null : standing[i]), itemStyle: { color: "#9d7bff" }, tooltip: { valueFormatter: money } }] : []),
          { name: "Export income", type: "bar", stack: "bill", data: rows.map(x => x.exportExact ? -Number(x.exportIncome) : null), itemStyle: { color: "#37d7a0" }, tooltip: { valueFormatter: money } },
          line("Energy Cost", net, "#ff5c70", "£")
        ];
        addEventMarkers(option, labels);
        billChart.setOption(option, true);
      }
    };

    periodButtons.forEach(btn => btn.addEventListener("click", () => {
      granularity = btn.dataset.period;
      if (granularity === "year") {
        range = "all";
        setActive(rangeButtons, range, "range");
      }
      setActive(periodButtons, granularity, "period");
      updateCards();
      render();
      renderSupplementary();
    }));

    metricButtons.forEach(btn => btn.addEventListener("click", () => {
      metric = btn.dataset.metric;
      setActive(metricButtons, metric, "metric");
      render();
    }));

    rangeButtons.forEach(btn => btn.addEventListener("click", () => {
      range = btn.dataset.range;
      setActive(rangeButtons, range, "range");
      render();
      renderSupplementary();
    }));

    const standingButtons = Array.from(document.querySelectorAll("[data-standing]"));
    standingButtons.forEach(btn => btn.addEventListener("click", () => {
      includeStanding = btn.dataset.standing === "included";
      setActive(standingButtons, includeStanding ? "included" : "excluded", "standing");
      renderSupplementary();
    }));

    document.querySelector("[data-reset-zoom]")?.addEventListener("click", () => {
      [chart, billChart].filter(Boolean).forEach(item => item.dispatchAction({ type: "dataZoom", start: 0, end: 100 }));
    });

    updateCards();
    render();
    renderSupplementary();

    const allocationRoot = document.getElementById("allocation-mix-chart");
    const latest = [...(data.supplierMonths || [])].reverse().find(x => x.complete);
    if (allocationRoot && latest) {
      const allocation = echarts.init(allocationRoot);
      charts.push(allocation);
      const bandNames = { ECO7_DAY: "Home day", ECO7_NIGHT: "Home night", EV_DEVICE_PEAK: "EV peak", EV_DEVICE_OFF_PEAK: "EV off-peak" };
      const palette = { ECO7_DAY: "#57b8ff", ECO7_NIGHT: "#9d7bff", EV_DEVICE_PEAK: "#ff6b8a", EV_DEVICE_OFF_PEAK: "#37d7a0" };
      allocation.setOption({
        backgroundColor: "transparent",
        animationDuration: 250,
        title: { text: latest.month, left: 0, top: 2, textStyle: { color: "#aeb9c8", fontSize: 12, fontWeight: 600 } },
        legend: { top: 0, right: 0, textStyle: { color: "#aeb9c8" } },
        tooltip: {
          trigger: "axis",
          axisPointer: { type: "shadow" },
          backgroundColor: "#0b111a",
          borderColor: "#33445c",
          textStyle: { color: "#edf2f8" },
          formatter: params => params.map(p => {
            const band = latest.bands.find(b => bandNames[b.band] === p.seriesName);
            return p.marker + p.seriesName + ": " + number(p.value, 3) + " kWh · " + money(band?.grossGbp);
          }).join("<br>")
        },
        grid: { left: 4, right: 4, top: 38, bottom: 6 },
        xAxis: { type: "value", show: false },
        yAxis: { type: "category", data: [latest.month], show: false },
        series: latest.bands.filter(b => bandNames[b.band]).map(b => ({
          name: bandNames[b.band],
          type: "bar",
          stack: "total",
          barWidth: 34,
          data: [Number(b.kwh)],
          itemStyle: { color: palette[b.band] },
          label: { show: Number(b.kwh) > Math.max(1, Number(latest.meterKwh) * 0.08), position: "inside", color: "#fff", formatter: () => number(b.kwh, 1) }
        }))
      });
    }
  };

  const initYearExplorer = () => {
    const data = parseJson("year-explorer-data");
    const root = document.getElementById("year-explorer");
    const body = document.getElementById("year-explorer-body");
    if (!data || !root || !body || !window.echarts) return;

    const chart = echarts.init(root);
    charts.push(chart);
    let metricKey = "cost";
    const buttons = Array.from(document.querySelectorAll("[data-year-metric]"));
    const colors = ["#57b8ff", "#9d7bff", "#37d7a0", "#ffb454", "#ff6b8a"];

    const formatValue = (value, unit) => {
      if (value == null) return "—";
      return unit === "£" ? money(value) : number(value) + " kWh";
    };

    const signed = (value, unit) => {
      if (value == null) return "—";
      const prefix = Number(value) > 0 ? "+" : "";
      return prefix + formatValue(value, unit);
    };

    const renderTable = metric => {
      body.textContent = "";
      metric.rows.forEach(row => {
        const tr = document.createElement("tr");
        const cells = [row.month];
        data.years.forEach(year => {
          const value = row.values.find(x => Number(x.year) === Number(year))?.value ?? null;
          cells.push(formatValue(value, metric.unit));
        });
        cells.push(signed(row.difference, metric.unit));
        cells.push(row.percentageDifference == null ? "—" : (Number(row.percentageDifference) > 0 ? "+" : "") + Number(row.percentageDifference).toFixed(1) + "%");
        cells.forEach((value, index) => {
          const td = document.createElement("td");
          td.textContent = value;
          if (index === 0) td.style.textAlign = "left";
          tr.appendChild(td);
        });
        body.appendChild(tr);
      });
    };

    const render = () => {
      const metric = data.metrics[metricKey];
      document.getElementById("year-explorer-title").textContent = metric.title;
      const option = chartBase();
      option.xAxis.data = metric.rows.map(x => x.month);
      option.yAxis.axisLabel.formatter = value => metric.unit === "£" ? "£" + number(value, Math.abs(value) < 20 ? 1 : 0) : number(value, Math.abs(value) < 20 ? 1 : 0);
      option.series = metric.series.map((series, index) => ({
        name: series.name,
        type: "line",
        showSymbol: true,
        symbolSize: 6,
        lineStyle: { width: 2, color: colors[index % colors.length] },
        itemStyle: { color: colors[index % colors.length] },
        emphasis: { focus: "series" },
        connectNulls: false,
        data: series.values,
        tooltip: { valueFormatter: value => formatValue(value, metric.unit) }
      }));
      chart.setOption(option, true);
      renderTable(metric);
    };

    buttons.forEach(btn => btn.addEventListener("click", () => {
      metricKey = btn.dataset.yearMetric;
      buttons.forEach(x => x.classList.toggle("active", x === btn));
      render();
    }));

    document.querySelector("[data-year-reset]")?.addEventListener("click", () => chart.dispatchAction({ type: "dataZoom", start: 0, end: 100 }));
    render();
  };


  initOverview();
  initYearExplorer();

  let resizeTimer;
  addEventListener("resize", () => {
    clearTimeout(resizeTimer);
    resizeTimer = setTimeout(() => {
      charts.forEach(chart => chart.resize());
        }, 120);
  });
})();
