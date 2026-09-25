# Mission pour le prochain agent : rendre OctetLedger intelligible et vérifiable

## Contexte et objectif

L'utilisateur a constaté que son stream du 15 septembre 2026 semblait absent des rapports : le trafic était pourtant enregistré sur Ethernet (455 MiB reçus, 4.48 GiB envoyés), tandis que la ligne Wi-Fi du même jour indiquait 5.11 GiB reçus et 1.14 GiB envoyés. L'ancienne sélection historique et l'absence de libellé d'interface dans le dashboard masquaient la différence. Les versions 0.6.1 et 0.6.2 ont respectivement corrigé la sélection des interfaces physiques et ajouté la colonne Interface au dashboard. Le README explique maintenant la distinction entre compteurs Windows et historique conservé. Le besoin restant n'est **pas** de retrouver ces octets : c'est de rendre la provenance, la couverture temporelle et l'interprétation de chaque chiffre évidentes dans le produit.

**But du travail :** un utilisateur doit pouvoir répondre, sans lire le code ni ouvrir la base SQLite, à « ce chiffre vient-il de Windows ou de l'historique ? », « quelles interfaces et quelle période couvre-t-il ? », « la collecte était-elle active ? » et « pourquoi le compteur Windows diffère-t-il du rapport ? ».

Ce document est une spécification de travail, pas une affirmation que les fonctions ci-dessous sont déjà implémentées. Priorité : clarté et exactitude des chiffres existants avant toute nouvelle métrique.

## État du dépôt à vérifier avant de modifier

- `src/OctetLedger.Cli/Program.cs` : `summary`/`interfaces` lisent les compteurs Windows, `live` mesure le débit ; `ShowReport` lit les données conservées, `ShowStatus` donne la dernière collecte et l'état du collecteur, `ShowHelp` oriente l'utilisateur.
- `src/OctetLedger.Cli/DashboardCommand.cs` : endpoint local `/api/data`, sélection des interfaces, agrégation journalière et HTML/JS du dashboard. Actuellement les cartes sont « 30-day total », « Daily average », « Peak day » et le tableau distingue les interfaces ; vérifier la sémantique des cartes et les périodes partielles avant de les renommer.
- `src/OctetLedger.Cli/ReportConsoleWriter.cs` : lignes de rapports, messages d'état du collecteur et avertissement sur le plus long intervalle observé.
- `src/OctetLedger.Core/TrafficStore.cs`, `TrafficReport.cs`, `NetworkInterfaceSelector.cs` : source des données, agrégation par période, filtre des interfaces physiques et traitement d'une préférence sauvegardée.
- `README.md`, surtout « How to read the numbers » ; `tests/OctetLedger.Tests/TrafficReportTests.cs` et `TrafficStoreTests.cs` pour les scénarios existants.
- Ne pas supposer que `interfaces` est un historique : il expose les compteurs courants de Windows, qui peuvent se réinitialiser indépendamment du redémarrage. Une première collecte ne mesure pas le trafic antérieur : elle établit une référence.

## Lot 1 — Provenance, périmètre et couverture (priorité haute)

1. **Compteurs directs.** Les sorties `summary` et `interfaces` doivent indiquer près des valeurs qu'il s'agit de compteurs Windows courants et qu'ils peuvent être remis à zéro lors d'une réinitialisation de l'adaptateur. `live` doit préciser qu'il mesure un débit entre deux lectures de l'interface affichée. Éviter de présenter ces valeurs comme une consommation journalière ou « depuis le démarrage du PC » garanti.
2. **Rapports conservés.** Les vues console `today`, `daily`, `monthly`, `total` et apparentées doivent rendre visibles leur source (différences de compteurs enregistrées par OctetLedger), leur période locale, les interfaces retenues et tout filtre explicite ou préférence sauvegardée. Conserver les lignes distinctes par interface ; `--all` inclut aussi les interfaces virtuelles/VPN et peut compter deux fois un même trafic. Respecter le format stable des exports JSON/CSV : ne pas introduire d'en-tête texte dans ces sorties.
3. **Dashboard.** Ajouter un libellé permanent expliquant que le tableau montre les interfaces physiques séparément, tandis que les cartes et barres représentent des totaux journaliers combinés pour les interfaces incluses ; si une préférence d'interface est sauvegardée, afficher que le périmètre est alors limité à cette interface. Rendre visibles la période affichée, l'heure de génération et l'heure de la dernière collecte réussie. Les dates doivent être explicites quant au fuseau utilisé, et la journée en cours doit être identifiable comme partielle.
4. **Absence de données.** Distinguer explicitement « aucune mesure enregistrée / première référence non encore suivie d'une collecte / collecteur arrêté ou retardé » d'un zéro mesuré. Ne jamais fabriquer un zéro sur une journée sans observations. Un long intervalle entre observations signale une attribution potentiellement imprécise dans le temps, **pas nécessairement une perte d'octets** : les compteurs peuvent couvrir le temps écoulé au prochain échantillon. Ne pas promettre une couverture exacte en minutes si le stockage ne permet pas de l'établir.

**Critères d'acceptation :** sur un historique avec Wi-Fi et Ethernet le même jour, le tableau nomme les deux interfaces ; carte/barre du jour = somme des lignes incluses ; le périmètre choisi est indiqué ; sans échantillons, la page et la CLI ne suggèrent pas « 0 octet consommé » ; si la collecte est en retard, l'information est visible avec l'heure de dernière réussite. Aucun format d'export existant n'est cassé.

## Lot 2 — Diagnostic orienté utilisateur (priorité après le lot 1)

Fournir, en réutilisant `status`, `doctor` ou une commande dédiée seulement si cela simplifie réellement l'usage, un parcours unique répondant à : interfaces actuellement vues par Windows ; interfaces présentes dans les données de la période demandée ; préférence enregistrée ou sélection automatique ; dernière collecte réussie et état du collecteur ; éventuel intervalle d'observation prolongé. Suggérer les commandes de comparaison (`interfaces`, `daily --all`, `daily --interface ...`, `status`) lorsque les chiffres semblent divergents. Expliquer que compteur Windows et historique enregistré ont des fenêtres et des remises à zéro différentes : **ne pas soustraire l'un de l'autre pour afficher un « trafic manquant » non prouvé**.

**Critère d'acceptation :** un scénario Wi-Fi → Ethernet permet de retrouver les octets Ethernet et d'expliquer pourquoi ils ne figurent pas dans une ligne Wi-Fi, sans requête SQLite manuelle. En cas de collecte inactive, donner une indication réparable ; ne jamais annoncer comme recueillies des données antérieures à la première référence.

## Lot 3 — Première utilisation et documentation (après l'implémentation)

Réorganiser l'aide/README autour de trois tâches : « mesurer le débit maintenant » (`live`), « voir mon historique » (`today`/`daily`/dashboard), « vérifier que la collecte fonctionne » (`status`/`doctor`). Préciser le délai de la première mesure, la persistance après redémarrage et les limites de `--all`. Ne pas dupliquer des paragraphes contradictoires ; mettre à jour le changelog pour tout changement utilisateur. Si le visuel `docs/images/dashboard.png` devient obsolète, le mettre à jour ou préciser qu'il est illustratif.

## Scénarios de validation et contraintes

- Cas Wi-Fi + Ethernet le même jour avec totaux différents : tableau séparé et graphique/carte cohérents ; préférence explicite Wi-Fi : aucun total Ethernet implicitement inclus ; rapport CLI `daily --all` : VPN visible, avec explication du risque de double comptage.
- Première collecte seulement, aucun intervalle mesuré ; collecteur absent/arrêté ; retardé ; collecte reprise après longue pause ; journée courante partielle ; historique couvrant un redémarrage ; base portable via `--data-dir` (ne pas indiquer à tort de démarrer le collecteur installé).
- Tester les vrais comportements (valeurs et états, pas seulement la présence d'un libellé). Utiliser une base isolée temporaire pour les tests, sans modifier la base utilisateur ; assurer la stabilité JSON/CSV ; compiler/tester et ouvrir le dashboard dans un navigateur avec jeu de données représentatif. Pour les libellés CLI, lancer réellement les commandes pertinentes. Aucun besoin de nouveau test si un scénario provisoire suffit, sauf pour les erreurs de calcul ou de sélection reproductibles.
- Préserver la confidentialité : dashboard lié à `127.0.0.1`, aucune télémétrie ou accès réseau ajouté ; ne pas déduire d'applications/sites à partir des octets. Éviter les changements de schéma sans nécessité démontrée. Ne pas publier de release ni modifier une version sans demande explicite.

**Ordre de travail :** relire le code et les tests actuels, établir précisément quels états/données sont disponibles, implémenter le lot 1 de bout en bout, puis le diagnostic et l'aide ; vérifier les cas ci-dessus et documenter toute limite technique réelle plutôt que d'afficher une précision inventée. Si le travail doit être découpé, chacun des lots est un jalon complet et vérifiable, mais aucun ne doit être annoncé terminé sur la seule base d'une maquette.
