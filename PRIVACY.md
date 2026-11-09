# Privacy Policy

## ScreenTranslate clipboard history (2026-11-08)

This fork adds local clipboard history. While ScreenTranslate is running, newly copied supported text, rich text, images and file references may be saved in its `clipboard-history` data folder, together with copy times, available source application names and pin state. Closing the history panel does not stop recording. Existing Windows history is read only when the user chooses to import it. History is retained until manually deleted; deleting a file reference does not delete the original file.

Record metadata and text use Windows user-scoped DPAPI protection. PNG image attachments are local files without additional encryption. File records contain paths, not backup copies. The application respects supported clipboard history and monitoring exclusion markers, but cannot identify every sensitive item that an originating application fails to mark.

When explicitly paired using the LAN feature, new supported text and images can be sent to the paired computer and retained in that computer's own history. Existing histories, deletions and pin state are not synchronized. This clipboard feature does not upload history to a cloud service or the developer. These local storage and paired-transfer behaviors qualify the inherited OverTranslate statements below; see [the feature's storage and validation limits](docs/Clipboard-Popup-2026-11-08.md).



_Last updated: 2026-08-21_



OverTranslate is a Windows screen translation tool.



## Data Collection



OverTranslate does not collect, store, track, or sell any personal information.



This application does not provide user accounts, analytics tracking, advertising tracking, or telemetry collection.



The one thing that is ever sent to the developer is the diagnostic file described under "Logs and Diagnostics" below, and only when the user presses the button that sends it.



## Screen Capture and OCR



OverTranslate may process screenshots locally on the user's device for OCR and translation purposes.



The screenshot or recognized text is used only to perform the translation requested by the user.



## Third-Party Translation Services



Depending on the translation provider configured by the user, recognized text may be sent to third-party translation services in order to generate translations.



These services may include providers such as Microsoft Translator, Bing Translator, Google Translate, Youdao Translate, Tencent Translate (TranSmart), Yandex Translate, DeepL, OpenAI, or other translation APIs configured by the user. Youdao Translate and Tencent Translate are operated from mainland China; text is only sent to them when the user chooses one of them as the translation service.



OverTranslate does not control how third-party services process data. Please refer to the privacy policy of the selected translation provider.



## API Keys



If the user configures an API key, the key is stored locally on the user's device for application functionality.



OverTranslate does not upload API keys to any server operated by the developer.



## Local Settings



Application settings may be stored locally on the user's device.



These settings are used only for application functionality.



## Logs and Diagnostics



OverTranslate writes a log file to the user's own device. It is never sent anywhere automatically.



When "Record detailed information" is switched on in Settings, the log also records text recognised on screen. That setting is off by default and is intended for troubleshooting.



Settings offers "Export and upload diagnostics", which packs the log files, information about the device, and the current settings into a single zip file stored with the application's own data on the user's device. API keys are replaced by a note giving only their length. That local copy is deleted automatically 30 days after it is created, whether or not it was uploaded.



Pressing that button also uploads the file and returns a short code the user can quote in a problem report. This happens only when that button is pressed. There is no automatic upload, no background upload, and no crash reporting. Whether the upload succeeds or fails, the file remains on the user's device, and a user who does not want to upload anything can still attach it by hand.



Uploaded files are held by Cloudflare (Workers KV) in an account operated by the project maintainer, under a name that cannot be worked out from the code shown to the user, and are deleted automatically 30 days after upload. No IP address or other identifier is stored with them: the only information kept alongside the file is the time of upload, the application version, and the operating system it reported. The code identifies an upload to the maintainer only — the application offers no way to browse or download uploaded files.



The source code of the receiving endpoint is public and can be read at https://github.com/Hon-Lu/OverTranslate-Diag-Worker



## Contact



For questions about this privacy policy, please contact the project maintainer through GitHub:



https://github.com/Hon-Lu/OverTranslate

