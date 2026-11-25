# BCF Manager

**Turn a model view into an issue in Navisworks Freedom.**

Spot something in the model, capture the view, and send your team an issue they can act on. BCF Manager is a small Windows app that sits beside Autodesk Navisworks Freedom and keeps your review work in one place.

![BCF Manager issue list open alongside a model in Navisworks Freedom](docs/images/navisworks-freedom.png)

## What you can do

- **Capture the view.** Create an issue with a model screenshot and, when available, the camera position and orientation. Use **Zur Ansicht** to prepare the saved view in Freedom, then click **OK** there to return to it.
- **Keep reviews organized.** Group issues by project, track their status, assign them to colleagues, and keep the discussion with the issue.
- **Share the result.** Exchange issues with BCF files or export a PDF report for a meeting or handover.

## Built for Freedom users

BCF Manager brings issue tracking to the free viewer many teams already use. Run it alongside Freedom and keep projects and issues on your PC, with no cloud account or Navisworks add-in required. The camera workflow is built for the German interface of Navisworks Freedom 2026.

<img src="docs/images/project-list.png" alt="BCF Manager project list" width="330">

<details>
<summary>Build and run on Windows</summary>

Run `./build.ps1` in PowerShell, then open `BCF-Manager.exe`. For automatic camera capture and view recall, place a Windows [Tesseract](https://tesseract-ocr.github.io/tessdoc/Installation.html) executable and its accompanying files under `tools/ocr/`, and [eng.traineddata](https://github.com/tesseract-ocr/tessdata_fast/blob/main/eng.traineddata) under `tools/tessdata/`.

</details>
