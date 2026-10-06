window.downloadFileFromStream = async (fileName, contentStreamReference, contentType = "application/vnd.ms-excel") => {
    const arrayBuffer = await contentStreamReference.arrayBuffer();
    const blob = new Blob([arrayBuffer], { type: contentType });
    const url = URL.createObjectURL(blob);

    const anchor = document.createElement("a");
    anchor.href = url;
    anchor.download = fileName;
    anchor.click();
    anchor.remove();

    URL.revokeObjectURL(url);
};


document.addEventListener("click", (event) => {
    document.querySelectorAll("details.rule-action-menu[open]").forEach((menu) => {
        if (!menu.contains(event.target)) {
            menu.removeAttribute("open");
        }
    });
});
