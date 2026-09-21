# Naik taraf Wasim Unity MCP Bridge ke 0.6.0

Pakej ini ialah salinan lengkap sumber GitHub v0.5.1 yang dinaik taraf. Semua 50 fail asal disertakan; GUID dalam fail .meta asal dikekalkan. Versi baharu menyediakan 38 tools: 21 tools asal dan 17 tools tambahan.

## Ganti kandungan GitHub

1. Ekstrak ZIP ke folder kosong.
2. Salin **semua kandungan ZIP ke root repository** wasim-unity-mcp-bridge. package.json mesti berada di root, bersama Editor/, Companion~/, Documentation~/ dan Tests/.
3. Gantikan fail bernama sama dan sertakan fail baharu serta fail .meta. Jangan gantikan Editor/ sahaja: skrip PowerShell companion turut berubah.
4. Commit perubahan ke branch anda. Selepas ujian Unity lulus, cipta tag v0.6.0 pada commit tersebut.

ZIP ini tidak mengubah atau menerbitkan repository GitHub secara automatik.

## Pasang dalam projek Unity

1. Hentikan companion melalui tetingkap bridge sebelum menukar versi. Simpan scene.
2. Dalam Package Manager, gunakan Add package from git URL:
   https://github.com/wasim-development/wasim-unity-mcp-bridge.git#v0.6.0
3. URL tersebut hanya boleh digunakan **selepas tag v0.6.0 diwujudkan**. Untuk menguji branch yang baru dikemas kini sebelum tag, gunakan URL yang sama dengan #main atau nama branch anda.
4. Rujukan lama #v0.5.1 akan kekal pada versi lama walaupun fail main sudah berubah.
5. Tunggu import/compilation selesai. Pakej memerlukan Unity 2022.3 dan menarik dependencies Newtonsoft JSON 3.2.1 serta Unity Test Framework 1.1.33 melalui Package Manager.
6. Buka Window > Wasim Development > Unity MCP Bridge, kemudian Start companion. Tetapan dan token lama menggunakan kunci projek yang sama.
7. Jalankan self-test. Versi ini memeriksa initialize, tools/list dan satu panggilan unity_get_status melalui IPC sebenar.
8. Refresh/reconnect MCP dalam aplikasi anda supaya katalog 38 tools dimuat semula. Server tidak menghantar notifikasi tools/list_changed melalui sambungan push.

Jangan tampal skrip pakej ini ke Assets. Gunakan folder package/repository, atau pemasangan package tempatan untuk ujian pembangunan.

## Pilihan baharu dalam Unity

- Allow reading package scripts: membolehkan bacaan package berdaftar, termasuk PackageCache.
- Allow reviewed change sets: membolehkan cadangan perubahan komponen, objek, skrip dan prefab.
- Allow reviewed Editor actions: membolehkan cadangan Play/Pause/Step/Compile/Test.

Dua pilihan perubahan baharu bermula dengan false. Proposal masih memerlukan Approve dalam Unity. Buka Window > Wasim Development > Change Sets and Actions untuk menyemak seluruh set. Butang Copy complete review JSON menyalin semua operasi untuk semakan.

## Batas operasi perubahan

- Sasaran perubahan komponen ialah objek dalam scene yang sudah disimpan dan dimuatkan. Simpan perubahan scene sebelum membuat proposal.
- Fail hanya ditulis di Assets; folder destinasi mesti sudah wujud.
- Cipta prefab menghasilkan asset baharu. Penyuntingan terus asset prefab sedia ada dan pemadaman objek tidak disediakan dalam v0.6.0.
- Skrip baharu mesti selesai compile sebelum kelasnya boleh ditambah sebagai komponen dalam proposal seterusnya.
- Proxy UdonSharp menggunakan adapter AddUdonSharpComponent/CopyProxyToUdon yang tersedia dalam SDK. Jika API tidak sepadan, bridge menolak operasi.
- Nilai property yang disokong: boolean, integer, nombor, string, enum, rujukan objek, Vector2/3/4, Quaternion dan Color. Edit elemen array secara berasingan; perubahan keseluruhan array/managed reference tidak disokong.

## Rollback

Pada set Applied, gunakan Prepare rollback atau unity_propose_change_set_rollback, kemudian semak dan Approve. Rollback memeriksa hash selepas perubahan; jika fail sudah diedit kemudian, ia berhenti supaya perubahan baharu tidak ditimpa.

Backup berada di Library/WasimUnityMcpBridge/ChangeSets/<id>/Assets/ dan rekod berada di index.json. Set menyimpan semula scene terlibat; rollback memuat semula scene yang dibuka. Simpan semua scene dahulu. Undo biasa hanya meliputi operasi objek yang masih berada dalam sesi Editor; untuk fail dan pemulihan selepas reload, gunakan rollback berasaskan backup.

Jika status RecoveryRequired muncul akibat crash/reload atau kegagalan pemulihan, jangan approve semula secara paksa. Semak index.json, bandingkan fail semasa dengan backup dan pulihkan hanya fail yang dimaksudkan. Backup di Library bukan pengganti commit Git dan hilang jika Library dipadam.

## Pengesahan

Baca VERIFICATION.md. Ujian .NET teras dijalankan semasa penyediaan; compilation keseluruhan di Unity dan ujian Windows/VRChat sebenar masih perlu dijalankan pada PC anda.
