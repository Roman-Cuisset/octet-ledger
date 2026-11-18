# Handoff — OctetLedger 0.11.0

## Étape 6B — validations natives Windows et migration 0.3.0 — 2026-11-18

Le workflow `Build` a été déclenché sur la seule branche `audit/0.11.0`. Le passage de référence teste le commit `6d566bd5b7792b5083662f310cb29cf7851f853a`. Le binaire issu de cette branche affiche `0.10.1` et le candidat conserve le schéma SQLite 6. [Passage de référence GitHub Actions](https://github.com/Roman-Cuisset/octet-ledger/actions/runs/44295775137)

| Environnement natif | Tests Release | Cycle de vie |
|---|---:|---:|
| Windows x64 | **243/243**, aucun test ignoré | **22/22**. [Rapports x64](https://github.com/Roman-Cuisset/octet-ledger/actions/runs/44295775137/artifacts/14424671046) |
| Windows 11 ARM64 | **243/243**, aucun test ignoré | **22/22**. [Rapports ARM64](https://github.com/Roman-Cuisset/octet-ledger/actions/runs/44295775137/artifacts/14424231582) |

Sur les deux architectures, le workflow a vérifié l’architecture native du runner et du runtime .NET, construit puis lancé l’exécutable du ZIP et validé l’installation, la collecte, la mise à jour, les rollbacks, les reprises du collecteur et la désinstallation. Les rapports TRX et JSON sont aussi archivés dans `artifacts/stage6b/run-44295775137/`.

La migration depuis le ZIP GitHub officiel `v0.3.0` est validée sur un runner jetable Windows Server 2022 x64, avec vérification préalable du moteur VBScript et de l’empreinte publiée du ZIP. L’installateur a démarré le collecteur sans appel manuel à `collector start`. Le même processus a enregistré deux collectes à **60,44 secondes** d’intervalle. Les réglages et les volumes historiques de la fixture sont conservés pendant le passage du schéma 0 au schéma 6. Le rollback après échec injecté et le rollback explicite après la migration restaurent le binaire officiel et le démarrage du collecteur en conservant les données. Le script refuse l’exécution si le profil GitHub hébergé contient déjà une installation. [Preuve de migration](https://github.com/Roman-Cuisset/octet-ledger/actions/runs/44295775137/artifacts/14423114470)

Les premiers passages ont isolé deux défauts dans la préparation du test du délai de mise à jour. Le SDK ARM64 refusait le minuteur de garde avec CA2027. Le lancement du processus enfant a ensuite dépassé son délai sur les deux runners. Le test final maintient un vrai tube de sortie ouvert après la sortie du processus, sans dépendre du lancement de cet enfant. Le calcul du délai d’exécution et de lecture reste le même. Les 15 tests de mise à jour ciblés et la suite Release locale **243/243** passent. [Premier passage](https://github.com/Roman-Cuisset/octet-ledger/actions/runs/44292090421) · [Deuxième passage](https://github.com/Roman-Cuisset/octet-ledger/actions/runs/44292922922)

Les cycles ont ensuite exposé deux verrous distincts. Le banc x64 réessaie la copie de la fixture de rollback en cas de verrou temporaire. Sur ARM64, un verrou a empêché le script livré de remettre en place le binaire courant après le rejet du candidat de rollback. La restauration réutilise désormais le remplacement atomique borné de la première permutation. Le test Windows maintient un vrai verrou, vérifie l’échec d’un remplacement unique puis la restauration et la conservation de la sauvegarde après nouvelle tentative. Le test ciblé passe sous PowerShell 7 et Windows PowerShell 5.1. Les rapports de cycle sont conservés si le nettoyage de la fixture échoue. [Échec x64 du banc](https://github.com/Roman-Cuisset/octet-ledger/actions/runs/44293808945) · [Échec ARM64 du rollback](https://github.com/Roman-Cuisset/octet-ledger/actions/runs/44294749599)

**Avis de publication** — Les deux blocages de l’étape 6 sont levés. Le candidat peut passer à la publication 0.11.0. Les transitions matérielles listées dans l’audit, le premier lancement d’un ZIP téléchargé et le parcours avec un lecteur d’écran restent non exécutés. Le parcours de mise à jour depuis l’artefact GitHub étiqueté 0.11.0 dépend de la future publication. Le numéro de version et WinGet sont hors de cette étape.

## Audit final de l’étape 6 — 2026-11-18

Branche `audit/0.11.0`. Les ZIP de validation contiennent des binaires qui affichent encore `0.10.1` et utilisent le schéma SQLite 6. Les preuves reproductibles de cette étape sont dans `artifacts/stage6/`.

L’audit a relu les changements du commit `b1cbe5c` à `f4b17af`, avec le README et le changelog. Les contrôles ont porté sur les captures réseau et la classification des interfaces, la couverture des rapports, la rétention et les écritures concurrentes dans SQLite, les sauvegardes et restaurations, le cycle de vie du collecteur et le dashboard.

### Défauts reproduits et corrections

| Défaut | Correction et preuve |
|---|---|
| La vérification de version attendait indéfiniment un flux de sortie hérité si le processus principal avait déjà quitté. | Le même délai borne la fin du processus et la lecture des deux flux. Un test reproduit le blocage avec un processus enfant réel et passe après correction. Voir `tests-regression-before/updater.trx` et la suite finale. |
| Une capture ETW à cheval sur deux heures imputait tous les octets à l’heure de fin. | L’horodatage de chaque événement détermine l’heure UTC. Écriture de la fenêtre en une transaction. Le test à minuit vérifie les volumes, les offsets différents et le regroupement par application quelle que soit la casse. Les volumes historiques conservent leur attribution d’origine. |
| Le contrôle de redémarrage échouait sur un horodatage JSON sous PowerShell en français. | Les dates relues sont converties et comparées comme des instants UTC. Le même démarrage donne `pending`. Un redémarrage réel du bac à sable donne `pass`, puis deux collectes réussies et les totaux préservés. Échec initial dans `sandbox-evidence/reboot-before-fix.json`. |
| Une mise à jour 0.3.0 échouée pouvait restaurer le binaire seul et un rollback explicite pouvait laisser la tâche et la clé de démarrage de la version récente. | Le script désenregistre le collecteur rejeté et réinstalle celui du binaire restauré après remplacement. Le rollback retire les inscriptions du collecteur sortant après le contrôle de compatibilité. Le ZIP publié de la 0.3.0 reproduit les deux anomalies avant correction, puis confirme leur disparition et des empreintes de base et de réglages identiques. Voir `sandbox-resumed/legacy-recovery-baseline.json` et `sandbox-resumed/legacy-recovery-final.json`. |
| Quatre vérifications Chromium ont échoué sous charge, entre délais dépassés et fichiers de profil temporaire encore ouverts. | Les essais Chromium s’exécutent les uns après les autres. Un délai explicite couvre le processus et ses flux. Le nettoyage réessaie après libération des fichiers. Le journal du passage ayant échoué est `tests-release/release-win-x64.trx`. |

### Validations et preuves

| Vérification | Résultat |
|---|---|
| Suite Release et format | **243/243** sous Windows x64, sans test ignoré. Rapport final `tests-audit/release-win-x64.trx`. Des passages antérieurs ont reproduit un dépassement du délai de préparation du test de mise à jour (`tests-final/release-win-x64.trx`) puis quatre échecs Chromium liés au banc de test (`tests-release/release-win-x64.trx`). Format .NET vérifié. Les huit scripts PowerShell passent l’analyse syntaxique sous 5.1 et 7.6. |
| Cycle de vie x64 | **21/21** après correction de l’installateur. L’essai supplémentaire exerce le rollback vers un collecteur sans tâche de surveillance ni inscription de démarrage récente. Les autres étapes couvrent notamment la reprise après arrêt imprévu ou blocage, les candidats incompatibles et la désinstallation. Voir `lifecycle-final-win-x64.json` et `lifecycle-final-host-observation.json`. Le candidat initial avait passé 20/20. |
| Mise à jour depuis le ZIP publié 0.10.1 | **Réussi en x64 natif**, en profil isolé. Première collecte, sauvegarde, mise à jour sur place, redémarrage du collecteur, rollback vers l’empreinte publiée et désinstallation. Les octets réseau et ETW de la fixture, le schéma 6 et les réglages sont vérifiés à chaque étape. Le PATH et le collecteur du profil courant sont préservés. Voir `native-upgrade-738942693aa844b19a2d97e2cce0a1cb/evidence.json`. |
| Mise à jour depuis le ZIP publié 0.3.0 | **Migration SQLite et récupération après échec réussies.** La copie de la base produite par le vrai binaire passe du schéma 0 au schéma 6. La ligne minute et l’état de l’interface sont conservés exactement, soit 39 208 octets reçus et 8 843 envoyés. Les deux bases passent `quick_check`. Le rollback vérifie aussi les empreintes et les inscriptions de démarrage. **Mise à jour réussie avec démarrage automatique non validée**. Le bac à sable n’a pas de moteur VBScript et l’installateur y échoue au lancement du nouveau collecteur. Voir `sandbox-resumed/historical-0.3.0-data-parity.json`, `sandbox-resumed/historical-0.3.0-install-error.txt` et `sandbox-evidence/vbs-probe.txt`. |
| SQLite, sauvegarde et concurrence | **Réussi.** Douze collectes CLI en concurrence, une sauvegarde pendant les collectes, un archivage manuel de contrôle, un second passage sans doublon et une restauration. Les 8 192 octets reçus et 2 048 envoyés de la fixture sont conservés. Intégrité OK pour les quatre bases contrôlées. La suite Release couvre la rétention automatique, les écritures concurrentes sur compteurs déterministes, le retour d’horloge et les restaurations bloquées ou récupérables avec WAL. Voir `sqlite-smoke-audit.json`. |
| IPv6 et redémarrage | **Réussi en bac à sable x64.** Le dernier ZIP mesure exactement 4 079 616 octets en émission et en réception TCP, et 5 898 240 dans chaque sens en UDP. Les deux sondes sur `::1` apparaissent dans `apps top`. Après redémarrage avéré, deux appels de collecte manuels réussissent et les totaux sont conservés. Voir `sandbox-resumed/ipv6-audit-win-x64.json` et `sandbox-resumed/reboot-final-win-x64.json`. |
| ZIP | **Deux ZIP contrôlés**. Lecture intégrale, SHA-256, architecture PE x64 et ARM64 et contenus comparés aux sources, dont `process.ps1`. Voir `zip-audit-verification.json`. |
| Dashboard | **Réussi en Chromium local** pour la politique HTTP, le rendu du texte, les trois modes et la navigation clavier. Le dossier de l’étape 5 conserve la vérification visuelle à 320 et 1120 px. Cet audit a relancé les tests et mesuré le vrai serveur HTTP local. |

Les deux ZIP historiques x64 utilisés correspondent aux SHA-256 publiés avec les téléchargements GitHub. L’exécutable ancien 0.3.0 utilise son profil Windows réel, ce qui a motivé son exécution exclusivement dans le bac à sable.

Les essais de cycle de vie, de migration et de redémarrage utilisent le ZIP `stage6-final`. Le dernier build `stage6-audit` ajoute le regroupement insensible à la casse entre heures dans `apps monitor`. Les contrôles ZIP, SQLite et IPv6 ont été rejoués avec ce dernier build.

### Mesures hôte x64

Médianes de cinq mesures après deux passages de chauffe sur la même copie de la fixture synthétique de l’étape 5. Les deux builds Release sont dépendants du framework. Les rapports incluent le démarrage du processus. Les mesures HTTP n’incluent pas le rendu du navigateur. Les deux réponses JSON du dashboard mesurent 30 393 octets. Les valeurs sont dans `dashboard-stage5-on-stage6-fixture.json` et `dashboard-stage6.json`.

| Opération | Étape 5 | Étape 6 |
|---|---:|---:|
| Horaire | 769 ms | 796 ms |
| Quotidien | 2 543 ms | 2 526 ms |
| Hebdomadaire | 5 266 ms | 5 170 ms |
| Mensuel | 5 582 ms | 5 311 ms |
| Total | 1 135 ms | 1 055 ms |
| Dashboard, HTML | 1,27 ms | 1,39 ms |
| Dashboard, `GET /api/data` | 1 490 ms | 1 522 ms |

Les timings varient entre les passages. Les écarts observés restent faibles et le code des rapports et du dashboard est identique à celui de l’étape 5.

### Commits locaux

| Commit | Objet |
|---|---|
| `cfa912c` | Délai de lecture des flux lors de la vérification de version. |
| `558619c`, `88fc32b` | Attribution ETW par heure UTC et regroupement par application. |
| `3f72301` | Normalisation des dates de preuve du redémarrage. |
| `dd8cee6` | Préparation des contrôles GitHub Actions sur la branche d’audit. |
| `00f0188` | Restauration des inscriptions Windows du collecteur sur rollback. |
| `6371bfa` | Fiabilité des essais Chromium et du nettoyage des profils temporaires. |

### Avant publication

La validation native ARM64 et la migration complète depuis 0.3.0 avec démarrage automatique restent à obtenir. Le workflow `Build` est préparé pour la branche d’audit avec permissions de lecture et déclenche les deux jobs natifs après un push autorisé. Le workflow `Release` reste déclenché par tag `v*`.

Les bascules réelles Wi-Fi/Ethernet, VPN et les sorties de veille ou d’hibernation restent non exécutées. L’avertissement au premier lancement d’un ZIP téléchargé est à vérifier manuellement. Le cycle x64 vérifie le marquage Internet simulé et le retrait de la marque lors de l’installation. L’accessibilité avec un lecteur d’écran dédié et le parcours de mise à jour via une release GitHub 0.11.0 restent à vérifier.

**Avis de publication** — Attendre le résultat ARM64 natif et un essai de mise à jour depuis 0.3.0 jusqu’à la première collecte sur un Windows x64 de test avec le moteur VBScript. Le passage de WinGet de 0.3.0 à 0.11.0 reste à traiter après la publication GitHub.

## Clôture locale de l’étape 5 — 2026-11-17

Branche `audit/0.11.0`, binaire `0.10.1`, schéma SQLite 6. Les preuves locales sont dans `artifacts/stage5/`.

- Le dashboard accepte la boucle locale à l’autorité exacte imprimée par la CLI et les méthodes GET et HEAD. Un hôte DNS, une cible HTTP absolue et les requêtes signalées comme interorigine sont refusés avant SQLite. La CSP limite les scripts, styles et connexions à la même origine. Les réponses désactivent la mise en cache, l’intégration en cadre et le reniflage MIME. [RFC 9110](https://www.rfc-editor.org/rfc/rfc9110.html#name-host-and-authority) · [Fetch Metadata](https://www.w3.org/TR/fetch-metadata/) · [CSP](https://www.w3.org/TR/CSP3/)
- Le HTML, le CSS et le JavaScript résident dans `src/OctetLedger.Cli/Dashboard/`. L’assembly les embarque et sert trois routes explicites.
- Les trois modes conservent leurs données, leurs couleurs et leurs marqueurs. Les boutons indiquent le mode actif. Le SVG expose un nom et un résumé par jour. Tab atteint le graphique, les flèches et Home/End déplacent le focus, et Échap ferme l’infobulle. Sur mobile, le graphique et les tableaux défilent dans leurs sections. [Navigation clavier WAI](https://www.w3.org/WAI/ARIA/apg/practices/keyboard-interface/) · [Contenu au survol ou au focus](https://www.w3.org/WAI/WCAG22/Understanding/content-on-hover-or-focus.html)
- Les totaux bruts et archivés sont agrégés par interface avant la jointure. La lecture des relevés réutilise les métadonnées d’interface, le filtre réutilise la classification d’un même pilote et les dates du format SQLite courant empruntent le parseur ISO rapide. Les autres formats historiques restent lisibles.

La suite Release x64 passe **241 tests sur 241**. `dotnet format --verify-no-changes --no-restore` passe également. Le rapport est `tests/release-win-x64.trx`. Les tests Chromium couvrent notamment les trois modes, le contenu hostile, la CSP, les états de mesure et la navigation clavier. Le dashboard réel a aussi été ouvert dans un navigateur avec une base synthétique. Les contrôles à 1120 px et 320 px ont vérifié le rendu, les touches réelles, le focus et le confinement du défilement et de l’infobulle.

Les exécutables autonomes `win-x64` et `win-arm64` ont été construits et leur architecture PE vérifiée. Le x64 a servi le HTML, le CSS et le JavaScript avec des empreintes identiques aux sources et la CSP attendue. L’API a renvoyé les données de 30 jours. Le résultat est dans `single-file-check.txt`.

### Mesures avant et après

Le script `scripts/benchmark-dashboard.py` utilise la bibliothèque standard de Python. La comparaison utilise la même base synthétique de 388 800 relevés minute, 3 000 agrégats journaliers et 129 600 observations, répartis sur trois interfaces. Le tableau donne la médiane de cinq mesures après deux passages de chauffe. Les temps CLI comprennent le lancement du processus et la sérialisation JSON. Les temps du dashboard mesurent les réponses HTTP sur loopback. Le temps de rendu du navigateur n’a pas été mesuré.

| Opération | Avant | Après |
|---|---:|---:|
| Rapport horaire, 24 h | 663 ms | 626 ms |
| Rapport quotidien, 30 j | 2 522 ms | 1 895 ms |
| Rapport hebdomadaire, 12 sem. | 4 877 ms | 3 676 ms |
| Rapport mensuel, 12 mois | 5 376 ms | 4 023 ms |
| Rapport total | 3 261 ms | 803 ms |
| Dashboard, `GET /api/data` | 1 596 ms | 1 142 ms |
| Dashboard, document HTML seul | 1,15 ms | 1,11 ms |

La référence est le commit `3efa77a`. Les mesures brutes sont dans `before-comparison.json` et `after-comparison.json`, et le profil par opération est dans `profile-before.txt` et `profile-after.txt`. Dans ce profil, la requête SQL du total passe de 2 360 à 296 ms, et la lecture des relevés sur 90 jours de 4 022 à 2 585 ms.

Les champs stables des cinq rapports JSON concordent ligne par ligne. Le débit moyen est vérifié par les tests à durée fixe, car il évolue avec le temps écoulé. Après la lecture des rapports, seuls deux compteurs d’en-tête SQLite ont avancé dans chaque copie. Les pages de données sont identiques à leur état initial et `PRAGMA quick_check` réussit dans les deux cas. La trace est dans `report-parity.txt`.

Pour reproduire la comparaison, exécuter le script avec le binaire avant et un répertoire neuf en passant `--seed --samples 5`. Exécuter ensuite le script avec le binaire après et le même répertoire en passant `--samples 5`.

### Commits de l’étape

| Commit local | Objet |
|---|---|
| `0afcd82` | Ressources HTML, CSS et JavaScript embarquées. |
| `e1b53ff` | Validation HTTP et en-têtes de sécurité. |
| `253686a` | Navigation clavier, infobulles accessibles et affichage mobile. |
| `5b2c843` | Optimisations des rapports SQLite et banc de mesure. |

### Validations restantes

La validation finale reste à effectuer sur ARM64 natif, ainsi que les transitions Windows et l’avertissement du premier lancement depuis un ZIP téléchargé. L’accessibilité a été vérifiée au clavier et dans l’arbre d’accessibilité Chromium. Le comportement avec un lecteur d’écran dédié reste à vérifier. La version, la publication et WinGet relèvent d’une étape ultérieure.

## Clôture locale de l’étape 4 — 2026-11-16

Branche `audit/0.11.0`, version du binaire `0.10.1`. Les preuves de clôture sont conservées dans `artifacts/stage4-finalization/`.

- La restauration refuse de créer une base si le fichier principal manque et que des fichiers de journal SQLite subsistent. Le nouveau test reproduit le défaut avec un vrai WAL et compare les octets du journal après le refus. Les cas de base ouverte, de WAL récupérable et de sauvegarde de la base précédente passent également. Le schéma reste en version 6.
- `install.ps1` conserve `CleanupDirectory` comme quatrième argument positionnel. `rollback.ps1` indique de restaurer une sauvegarde compatible si la base est illisible pour la version précédente et conserve le diagnostic distinct en cas d’expiration.

| Vérification | Résultat de clôture |
|---|---|
| Suite Release x64 | **225/225**, dont les 6 tests `RestoreSafetyTests`. Rapport `dotnet/release-win-x64.trx`. |
| Format et syntaxe | `dotnet format --verify-no-changes` et `git diff --check` passent. Les 8 scripts PowerShell ont été analysés sous Windows PowerShell 5.1 et PowerShell 7. La liaison du quatrième argument a aussi été vérifiée sous Windows PowerShell 5.1 avec une source absente. |
| ZIP locaux | `packages/OctetLedger-0.10.1-stage4-win-x64.zip` et `packages/OctetLedger-0.10.1-stage4-win-arm64.zip`. Les empreintes SHA-256, l’architecture PE des exécutables et le contenu des scripts installés correspondent aux builds et aux sources. |
| Cycle de vie x64 | **20/20** sur le ZIP final, dans un profil temporaire de l’hôte. Le test couvre l’appel positionnel, le diagnostic de base incompatible, la conservation des réglages et des statistiques SQLite après échec et après rollback, la récupération du collecteur et le retrait de la marque Internet simulée. Le collecteur habituel a conservé son PID et le PATH utilisateur a été rétabli. Preuves `lifecycle-win-x64.json` et `lifecycle-host-observation.json`. |
| IPv6 ETW élevé x64 | **PASS** sur le binaire du ZIP final dans Windows Sandbox. TCP et UDP en boucle locale apparaissent dans `apps top --count 100`. L’empreinte du binaire correspond au ZIP. Preuve `sandbox-evidence/ipv6-win-x64.json`. |

| Commit local | Objet |
|---|---|
| `3fdd3fd` | Protection de la restauration SQLite et six tests ciblés. |
| `47e2d74` | Tests Windows natifs dans la CI et sélection du navigateur. |
| `f65d867` | Initialisation du fournisseur noyau ETW et validation live IPv6. |
| `54f6737` | Délais, retrait de la marque Internet, compatibilité de l’installation et cycle de vie. |

Les validations restantes portent sur l’exécution native et le cycle de vie ARM64, les transitions réelles d’adaptateur, de VPN et d’alimentation, et le premier avertissement à l’ouverture d’un ZIP téléchargé. Le changement de version, le tag, la release et WinGet sont hors de cette clôture locale.

## Première reprise dans Codex — historique du 2026-11-16

Les résultats ci-dessous utilisent les archives de la première reprise. Les sections numérotées conservent l’état transmis par OMP.

- Le test IPv6 élevé a reproduit un échec au démarrage d’`apps monitor`. Le fournisseur noyau ETW est maintenant activé avant le premier accès à `TraceEventSession.Source`, y compris pour les événements de démarrage et d’arrêt des processus.
- Edge local quittait avec le code 0 et sans DOM, y compris sur une page minimale. Les tests recherchent désormais Chrome avant Edge et acceptent toujours `OCTETLEDGER_TEST_BROWSER`. Les deux navigateurs figurent sur l’image Windows 11 ARM64 de GitHub. [Inventaire de l’image](https://github.com/actions/runner-images/blob/main/images/windows/Windows11-VS2026-Arm64-Readme.md)
- Le test de cycle de vie exige l’erreur attendue, distingue les six échecs d’installation et de rollback dans le JSON et revérifie les données après un rollback réussi. Le délai de la fixture passe à 10 secondes pour laisser le collecteur sain s’arrêter et redémarrer.
- Le README et le changelog documentent le délai des scripts, le retrait de la marque Internet de la copie installée et les conditions de restauration SQLite.

Les preuves de reprise se trouvent dans `artifacts/resume-0.11.0/`. Les archives et les empreintes SHA-256 sont destinées à la validation locale.

| Vérification | Résultat de la reprise |
|---|---|
| Suite Release et format sur Windows x64 | **224/224**, dont les 5 tests de restauration SQLite. `dotnet format --verify-no-changes` et `git diff --check` passent. |
| Syntaxe PowerShell | 8 scripts analysés sous Windows PowerShell 5.1 et PowerShell 7. |
| ZIP x64 et ARM64 | Construits localement, présence de `process.ps1` et empreintes vérifiées. Archives `packages/OctetLedger-0.10.1-audit-final-win-*.zip`. |
| Cycle de vie Windows x64 | **20/20** avec le ZIP `OctetLedger-0.10.1-audit-win-x64.zip`. Le lanceur VBScript échouait dans Windows Sandbox. Le cycle complet a été rejoué sur l’hôte dans un profil de test temporaire, avec sa propre tâche planifiée. Le PID du collecteur existant et le PATH utilisateur ont été vérifiés avant et après. Preuve `lifecycle-host-win-x64.json`. |
| IPv6 ETW élevé x64 | **PASS** sur TCP et UDP en boucle locale dans Windows Sandbox. La sonde apparaît dans `apps top --count 100` pour les deux transports. Preuve `sandbox-evidence/ipv6-final-win-x64.json`. |

## État transmis par OMP

État figé pour reprise dans un autre outil (Codex). Document écrit sans supprimer ni modifier le travail existant.

- Date : 2026-11-16
- Branche : `audit/0.11.0` (aucun push, aucun merge, aucun tag, aucune release)
- Base : `b1cbe5c` (`style: fix indentation in NetworkInterfaceSelector`)
- Version dans `src/OctetLedger.Cli/OctetLedger.Cli.csproj` : **0.10.1** — pas encore passée à 0.11.0
- WinGet : version publiée **0.3.0** ; mise à jour vers 0.11.0 prévue **après** la release GitHub

## 1. Règles à conserver

- Ne pas pousser, fusionner ni publier sans autorisation explicite.
- Ne pas casser les fonctionnalités existantes, ne pas migrer SQLite sans justification.
- Préserver les données locales et la compatibilité des commandes publiques.
- Sous-agents : même modèle et fournisseur que l'agent principal.
- Ne rien supprimer des fixtures/artefacts temporaires listés en §8.
- Manifeste WinGet à préparer uniquement après publication GitHub de la 0.11.0.

## 2. Commits déjà réalisés (6)

| Commit | Contenu |
|---|---|
| `08244a3` | `fix(dashboard): render local timeline data as text` — étape 1, P0 sécurité |
| `79e0441` | `fix(collector): serialize captures and reject stale frames` — étape 1, P0 stats |
| `a3224b5` | `fix(dashboard): preserve missing days and bound trend smoothing` — étape 2 |
| `38521da` | `fix(dashboard): scope interruption markers to selected interfaces` — étape 2 |
| `7c5aa5d` | `fix(retention): apply configured daily SQLite maintenance` — étape 3 |
| `a0a26ca` | `fix(network): classify adapters by driver identity and bounded markers` — étape 3 |

### Étape 1 — P0

- Dashboard : plus aucune concaténation de données locales dans `innerHTML` pour les timelines ; construction DOM avec `textContent`. Régression navigateur réelle (`DashboardSecurityTests`).
- Collecte : `CollectCurrent(Func<…>)` acquiert le verrou d'écriture SQLite **avant** la lecture des compteurs ; les trames pré-capturées périmées/répétées sont ignorées sans toucher aux baselines ni à l'historique. Tests : `CollectionOrderingTests` (8 cas).

### Étape 2 — graphiques

- Trend : segments interrompus aux jours non mesurés, points isolés conservés, surface coupée.
- Catmull-Rom borné aux valeurs mesurées voisines (plus de passage sous zéro).
- Zéro mesuré (y compris archive journalière sans observations brutes) ≠ absence de mesure ≠ baseline seule.
- Interruptions : marqueur plein = collecte, cercle creux = interface du périmètre ; interface virtuelle exclue ignorée ; couverture inconnue séparée.
- Tests : `DashboardChartTests`, `DashboardCoverageTests`, helper `DashboardBrowser`.

### Étape 3 — rétention et interfaces

- `AutomaticRetention` : vérification à la première collecte réussie puis au changement de date locale ; `collect` vérifie à chaque succès ; contrôles indexés (`onlyIfDue`) pour éviter de réarchiver au redémarrage ; transaction unique ; reprise après interruption.
- `NetworkInterfaceSelector` : identité basée sur la description du pilote (repli nom), marqueurs délimités, `WAN Miniport` au lieu de `WAN`/`Miniport`, ajout `ISATAP`/`OpenVPN`/`Wintun`. Préférences explicites et `--all` inchangés.
- Tests : `AutomaticRetentionTests` (8), `NetworkInterfaceClassificationTests`.

## 3. Étape 4 — modifications locales au moment du transfert OMP

10 fichiers modifiés, 2 nouveaux. `git diff --stat` : +234/−37 avant les derniers ajouts.

### 3.1 Nouveau : `scripts/process.ps1` (untracked)

`Invoke-OctetLedgerExecutable` : exécution bornée d'un exe Windows.

- Quoting CRT (espaces, guillemets, backslashes finaux, argument vide).
- Lecture stdout/stderr asynchrone puis attente avec échéance.
- Timeout → arrêt de l'arbre (`Kill($true)` en PowerShell 7 ; `taskkill /PID … /T /F` borné en 5.1), puis `TimeoutException`.
- Exit code ≠ 0 → exception contenant stderr/stdout ; met `$global:LASTEXITCODE`.
- Vérifié réellement sous **Windows PowerShell 5.1** et **pwsh 7** avec un exe fixture : aller-retour exact des arguments, timeout de 2 s, enfant tué.

### 3.2 `scripts/install.ps1`

- Paramètre `-ExecutableTimeoutSeconds` (défaut 60, 1–300).
- Dot-source `process.ps1` (échec explicite si absent du package).
- Appels bornés : `version` du binaire stagé, `collector stop`, `collector install`, chemins de rollback (`stop`/`start`/`uninstall`).
- L'erreur d'origine est conservée (`$failure = $_`) et relancée après compensation.
- `process.ps1` copié dans le répertoire d'installation (rollback installé en dépend).
- `Unblock-File` **sur la copie installée uniquement**, après remplacement réussi et après restauration de l'exécutable précédent.
  - Objectif : supprimer l'avertissement répété « Open File – Security Warning » (marque Internet héritée du ZIP).
  - Ne désactive ni SmartScreen ni les politiques Windows ; ne crée pas d'identité d'éditeur.

### 3.3 `scripts/rollback.ps1`

- Même paramètre, même helper, appels bornés (`version` précédent, `database check`, `collector stop/install/start`).
- `Unblock-File` après remplacement et après restauration de secours.
- Erreur d'origine préservée.

### 3.4 `scripts/package.ps1`

- Copie `process.ps1` dans le ZIP. **Sans cette copie, install/rollback échouent.**

### 3.5 `scripts/test-install-lifecycle.ps1`

- `$testRoot` contient désormais un espace (couverture de quoting).
- `Assert-InstalledZoneMarker` : le binaire téléchargé garde `Zone.Identifier`, la copie installée ne l'a plus, un flux nommé tiers est préservé.
- `Assert-PreservedInstallation` : hash de l'exe + hash de `settings.json` + statistiques applicatives seedées (`database seed`/`verify` via la fixture).
- `Test-CandidateFailure` : rejette un exe qui pend ou échoue, vérifie l'arrêt du processus, l'absence de remplacement et la conservation des données.
- Scénarios ajoutés : `hang-version`, `fail-install`, `hang-install` pour l'installateur ; `hang-check`, `fail-install`, `hang-install` pour le rollback.
- Fixture publiée en self-contained **avec** `IncludeNativeLibrariesForSelfExtract` (nécessaire car elle référence `OctetLedger.Core`).
- Aller-retour d'arguments natifs (espaces/guillemets/backslash/chaîne vide).

### 3.6 `scripts/validation-rollback/Program.cs` + `.csproj`

- Nouvelle référence `OctetLedger.Core`.
- Modes via `OCTETLEDGER_VALIDATION_MODE` : `echo`, `hang-version`, `hang-install`, `hang-check`, `fail-install` ; fichier PID pour prouver l'arrêt.
- `database seed <db>` / `database verify <db>` : écrit puis vérifie 4 096 / 1 024 octets pour `octetledger-lifecycle-preserved.exe`.
- Comportement historique conservé : `version` OK, `database check` → exit 1 avec marqueur `rollback-validation-database-check`.
- Compilé avec succès (`dotnet build scripts/validation-rollback/...`).

### 3.7 `scripts/test-windows-ipv6.ps1`

- Correction du défaut d'audit : `apps top --count 10000` → `--count 100` (limite publique).
- Exécution réelle : **exit 2 / BLOCKED**, car terminal non élevé. C'est le comportement attendu, pas un PASS.

### 3.8 `.github/workflows/build.yml` et `release.yml`

- `build.yml` : `dotnet test` sur le runner **ARM64** avant publication/package.
- `release.yml` : `dotnet test` sur chaque architecture du matrix avant le packaging.
- **Risque non validé** : les tests dashboard lancent Edge headless. Si le runner ARM64 n'a pas Edge, la CI échouera. À vérifier avant de pousser.

### 3.9 `src/OctetLedger.Core/TrafficStore.cs` — `Restore`

Ajout de `LockRestoreDestination` + garde autour du remplacement :

- Ouvre la destination en `FileShare.Delete` : toute connexion SQLite ouverte (collecteur, dashboard, test) bloque la restauration **avant** toute modification.
- Erreur explicite : « The destination database is in use… database was not replaced. »
- Si des journaux `-wal`/`-journal` non vides subsistent (arrêt brutal), réouverture SQLite sans initialisation de schéma, `PRAGMA schema_version` puis `journal_mode=DELETE` pour consolider ; nouvelle garde ; refus si des journaux restent ; suppression des sidecars résiduels.
- Comportements existants conservés : vérification d'intégrité source, copie via `BackupDatabase`, vérification de la copie, `File.Replace` avec sauvegarde `octetledger.before-restore-*.db`.

### 3.10 Nouveau : `tests/OctetLedger.Tests/RestoreSafetyTests.cs` (untracked, 5 tests, tous verts)

1. Connexion collecteur/dashboard active → `IOException`, données inchangées.
2. WAL orphelin consolidé avant remplacement ; sauvegarde précédente contient les pages WAL (400 B) ; destination restaurée (700 B).
3. Connexion WAL active → refus ; la connexion continue de fonctionner sur sa base ; source intacte.
4. Fichier principal corrompu conservé tel quel dans la sauvegarde avant remplacement.
5. Échec de remplacement (lecteur ouvert) → octets inchangés, reprise réussie après fermeture.

## 4. Vérifications exécutées avant le transfert OMP

| Vérification | Résultat |
|---|---|
| Suite complète après étape 3 | **219/219** |
| `RestoreSafetyTests` après la garde | **5/5** |
| Compilation `OctetLedger.Core`/`Cli`/`Tests` (via tests) | OK |
| Compilation `validation-rollback` | OK, 0 avertissement |
| Parsing PowerShell 5.1 + 7 de tous les `.ps1` | OK (rejoué au moment de la passation) |
| `git diff --check` | propre |
| Helper borné sous 5.1 et 7 (args + timeout + kill) | PASS |
| Refus de restauration avec connexion ouverte | exit 1, message « in use », dashboard toujours à 100 B |
| Restauration après fermeture | exit 0, 700 B, sauvegarde 100 B, intégrité `ok` |
| IPv6 | BLOCKED sans élévation (exit 2) — **pas un pass** |

## 5. Vérifications ouvertes au moment du transfert OMP

- Suite complète après les modifications de l'étape 4 (attendu ~224 tests).
- `dotnet format OctetLedger.slnx --verify-no-changes --no-restore`.
- `test-install-lifecycle.ps1` complet (nouveaux scénarios timeout/ADS/données) sur VM Windows jetable.
- `test-windows-ipv6.ps1` en terminal **élevé** avec IPv6/ETW disponible.
- `test-windows-transition.ps1` (adapter-switch, VPN, sleep, hibernate, reboot) sur machine de test.
- `.NET tests` sur ARM64 et validation des runners ARM64.
- Comportement réel de l'avertissement Windows sur un ZIP téléchargé (voir §7).

## 6. Ordre de reprise proposé par OMP

1. `dotnet test OctetLedger.slnx -c Release` puis `dotnet format … --verify-no-changes`.
2. Vérifier que `process.ps1` est bien dans les packages (`scripts/package.ps1`).
3. Publier un package de test et exécuter `scripts/test-install-lifecycle.ps1` sur VM Windows jetable ; consulter l'evidence JSON (les nouveaux noms d'étapes doivent apparaître).
4. Valider IPv6 en élevé ; sinon conserver BLOCKED dans le bilan.
5. Vérifier Edge sur le runner ARM64, ou rendre les tests navigateur explicitement skip/fail-fast selon disponibilité.
6. Mettre à jour `README.md` (timeouts, déblocage de la copie installée, premier lancement du ZIP) et `CHANGELOG.md` (section étape 4).
7. Commits séparés suggérés :
   - `fix(install): bound executable calls and clear installed Internet mark` (+ `process.ps1`, `package.ps1`, fixture, lifecycle)
   - `fix(database): refuse restore while clients hold the database` (+ `TrafficStore.cs`, `RestoreSafetyTests`)
   - `test(windows): fix IPv6 report limit and run native tests` (+ scripts/workflows)
8. Seulement ensuite : bump 0.11.0, changelog final, tag, release GitHub.
9. Après release : manifeste WinGet 0.3.0 → 0.11.0 + SHA-256 des deux ZIP.

## 7. Risques et points d'attention

- **`process.ps1` manquant = install/rollback cassés.** Tout package construit sans la copie mise à jour échoue au dot-source.
- **Avertissement Windows** : `Unblock-File` traite la marque Internet de la copie installée. Il ne supprime pas le premier avertissement possible de `install.cmd` dans un ZIP fraîchement téléchargé (problème d'œuf et de poule) et ne remplace pas une signature Authenticode. Documentation à ajuster en conséquence.
- **Timeout 60 s par défaut** : configurable, mais `collector install` peut être lent sur machine chargée ; surveiller les faux timeouts.
- **Garde de restauration** : toute connexion ouverte bloque, y compris une lecture longue du dashboard ; message explicite. `FileShare.Delete` est une sémantique Windows ; les tests CI sont Windows uniquement.
- **Consolidation WAL** : `journal_mode=DELETE` est appliqué à la destination avant remplacement ; en cas d'échec, la base d'origine est conservée mais son mode journal peut avoir changé.
- **ARM64 + tests navigateur** : risque de CI rouge si Edge absent.
- **Chemin de test avec espaces** : bon pour la couverture, mais peut révéler des quoting non traités dans d'autres scripts.
- **Pas de migration SQLite** dans toute la mission ; schéma 6.

## 8. Fixtures et artefacts laissés en place (ne pas supprimer)

- `C:\Users\stcui\AppData\Local\Temp\octetledger-step4-ekzvvdhy\`
  - `RestoreSmoke.cs` (programme de smoke restauration)
  - `ipv6.json` (evidence BLOCKED)
  - `helper.pid`
  - `publish\` (sortie de publish)
  - `restore\octetledger.db` (700 B restaurés), `restore\source.db` (700 B), `restore\octetledger.before-restore-20261111-190937-5bca9ccd.db` (100 B, intégrité ok)
- Artefacts préexistants (sessions antérieures, ne pas confondre) :
  `octetledger-final-package-*`, `octetledger-final-publish-*`, `octetledger-lifecycle-*`, `octetledger-package-check-*`, `octetledger-publish-*`, `octetledger-release-verify-*`, `octetledger-tests-*`
- `artifacts/validation/lifecycle-smoke.json` (preuve 0.7.1, historique)

Aucun service/job de l'agent n'est resté actif. Le processus `octetledger.exe` PID 19528 est le **collecteur installé de l'utilisateur** (`%LOCALAPPDATA%\Programs\OctetLedger`) : ne pas l'arrêter sans demande.

## 9. Reste de l'audit (hors étapes 1–4)

- Durcissement HTTP du dashboard (Host/Origin/méthode), CSP après extraction des ressources.
- Extraction HTML/CSS/JS, accessibilité (clavier, `aria-pressed`, nom du SVG), axe mobile ~3,4 px.
- Attribution des fenêtres ETW à cheval sur une heure.
- Interface tray facultative : conception seulement, non implémentée.
- Mise à jour de `docs/` et du screenshot si le dashboard change encore.

## 10. Reprise rapide

```powershell
git switch audit/0.11.0
git status --short --branch
dotnet build OctetLedger.slnx -c Release
dotnet test OctetLedger.slnx -c Release --no-build
dotnet format OctetLedger.slnx --verify-no-changes --no-restore
```
