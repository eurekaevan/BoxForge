"use strict";

const form = document.querySelector("#export-form");
const fileInput = document.querySelector("#yaml-file");
const fileNote = document.querySelector("#file-note");
const nameInput = document.querySelector("#configuration-name");
const dropZone = document.querySelector("#drop-zone");
const submitButton = document.querySelector("#submit-button");
const status = document.querySelector("#status");

function setStatus(message, state = "") {
  status.textContent = message;
  if (state) {
    status.dataset.state = state;
  } else {
    delete status.dataset.state;
  }
}

function updateFileNote() {
  const file = fileInput.files[0];
  fileNote.textContent = file
    ? `${file.name} · ${(file.size / 1024).toFixed(1)} KiB`
    : "尚未选择文件";
}

fileInput.addEventListener("change", updateFileNote);

for (const eventName of ["dragenter", "dragover"]) {
  dropZone.addEventListener(eventName, event => {
    event.preventDefault();
    dropZone.classList.add("is-over");
  });
}

for (const eventName of ["dragleave", "drop"]) {
  dropZone.addEventListener(eventName, event => {
    event.preventDefault();
    dropZone.classList.remove("is-over");
  });
}

dropZone.addEventListener("drop", event => {
  if (event.dataTransfer.files.length === 1) {
    fileInput.files = event.dataTransfer.files;
    updateFileNote();
  } else {
    setStatus("请一次只选择一个文件。", "error");
  }
});

form.addEventListener("submit", async event => {
  event.preventDefault();

  const file = fileInput.files[0];
  const platforms = [
    ...form.querySelectorAll('input[name="platforms"]:checked')
  ];
  if (!file) {
    setStatus("请先选择 YAML 文件。", "error");
    fileInput.focus();
    return;
  }

  if (platforms.length === 0) {
    setStatus("请至少选择一个目标平台。", "error");
    return;
  }

  const body = new FormData();
  body.append("file", file, file.name);
  if (nameInput.value.length > 0) {
    body.append("name", nameInput.value);
  }
  for (const platform of platforms) {
    body.append("platforms", platform.value);
  }

  submitButton.disabled = true;
  setStatus("正在转换并构建 ZIP……");
  try {
    const response = await fetch("/api/v1/export", {
      method: "POST",
      body
    });
    if (!response.ok) {
      let message = `转换失败（${response.status}）。`;
      const contentType = response.headers.get("content-type") || "";
      if (contentType.includes("application/problem+json")) {
        const problem = await response.json();
        if (problem.detail) {
          message = problem.detail;
        }
      }
      throw new Error(message);
    }

    const archive = await response.blob();
    const downloadUrl = URL.createObjectURL(archive);
    const link = document.createElement("a");
    link.href = downloadUrl;
    link.download = "boxforge-output.zip";
    document.body.append(link);
    link.click();
    link.remove();
    setTimeout(() => URL.revokeObjectURL(downloadUrl), 1000);
    setStatus("转换成功，ZIP 已开始下载。", "success");
  } catch (error) {
    setStatus(error.message || "转换失败。", "error");
  } finally {
    submitButton.disabled = false;
  }
});
