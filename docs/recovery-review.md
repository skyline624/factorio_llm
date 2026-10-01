# Reprises après la relecture du 1er octobre 2026

Six défauts de reprise ont été corrigés avant de poursuivre les campagnes. Les corrections restent en C# ; elles ne changent ni le modèle principal ni la visibilité native.

## Comportements corrigés

- **Cellule de ressources choisie.** Le directeur passe l'identifiant exact de la cellule au constructeur. Une cellule devenue indisponible est refusée ; le constructeur ne reprend pas une autre cellule du même produit à sa place.
- **Plan absent ou partiel.** Chaque cellule conserve tous ses équipements calculés, avec ses poteaux de liaison. Pour les anciens registres, le directeur reconstitue le plan depuis la rangée, l'emplacement et la géométrie native, avant d'évaluer les zones de mort. Une rangée introuvable ne devient pas implicitement sûre. Une cellule différée pour danger ne consomme aucune tentative de reconstruction. La maintenance applique le même report aux cellules de ressources prêtes avant tout déplacement ou reconstruction.
- **Limite cartographique.** Une lecture tronquée peut contenir des secteurs à la même distance que le premier secteur omis. Ils sont acceptés. La couverture complète reste strictement à l'intérieur de cette distance : la mémoire ne supprime pas les destinations absentes du dernier anneau.
- **Radar interrompu.** La cellule et les placements sont persistés avant les constructions, puis les identifiants natifs après les reçus. La reprise adopte une entité propre correspondant au plan. Un ancien radar propre non enregistré peut aussi être réutilisé. Les poteaux construits pour le raccordement suivent le même contrat.
- **Cellule en construction attaquée.** Le constructeur réconcilie les rôles avec la photographie native connue avant de procurer les pièces. Il retire les identifiants disparus et adopte les remplacements correspondant au nom, à la position et à l'orientation prévue, sans prendre une entité réservée par une autre cellule.
- **Silo remplacé manuellement.** Le lancement réconcilie les cellules de silo avant de choisir un silo isolé. Un nouveau silo à l'emplacement prévu reste alimenté par le coffre et le bras de sa cellule ; il ne passe pas au chemin d'insertion directe.

Les cellules prêtes gardent les rôles manquants dans leur registre pour que la maintenance sache encore les reconstruire. Les cellules en construction retirent leurs identifiants périmés pour que la reconstruction et l'approvisionnement comptent correctement les pièces nécessaires.

## Vérifications hors ligne

Compilation Release avec le SDK local : zéro erreur et zéro avertissement. Les tests finaux passent : 936 tests Host, 89 Ollama et 37 Infrastructure, soit **1 062 réussites**. Un test cloud optionnel reste désactivé.

Les régressions couvrent notamment le directeur avec deux cellules du même produit, dont une ancienne sans plan dans une zone de mort ; la reconstruction d'un plan limité à un poteau ; le budget cartographique natif de 64 dépôts coupé au milieu de l'anneau à 128 cases, ainsi qu'une coupure à distance zéro ; les identifiants détruits ou remplacés pendant une construction ; et le nouveau silo conservant son alimentation de cellule.

## Vérifications Factorio 2.0.77

Les quatre rapports ci-dessous portent `passed=true` et `isAutonomousCampaign=false`. Ils sont conservés avec leurs journaux sous `.runtime/`, hors Git. Les fixtures préparent explicitement terrain, recherches, ressources, équipements ou énergie ; leurs lancements ne comptent pour aucune qualification finale.

| Essai | Graine | Preuve obtenue |
| --- | --- | --- |
| `verify-resource-cells`, headless | 20261001 | Deux cellules de fonte et une de charbon. 30 charbons collectés puis fournis aux fours ; 36 plaques de fer collectées. Poteau détruit reconstruit en place et foreuse épuisée retirée de la capacité. |
| `verify-charted-resources --radar`, headless | 20261002 | Interruption volontaire après le reçu natif de construction du radar, avant le retour de son identifiant. Une cellule `building` conserve son plan et un seul radar existe. La reprise termine cette cellule sans doublon ; le radar révèle le pétrole, l'extraction débloque `oil-processing` au tick 21 964. |
| `verify-silo-cell`, headless | 20261002, même fixture | Lancement préparé et reconstruction du bras pendant le lancement. Silo détruit puis restauré, remplacement manuel adopté dans la même cellule, poteau détruit d'une cellule marquée `building` remplacé sans second silo ni insertion directe. |
| `verify-silo-cell`, client connecté | 20261002, même fixture | Même qualification complète avec un joueur connecté au même personnage, vérifié nativement par la préparation. Compteur de lancements de 1 à 2 ; remplacement manuel et reprise après destruction du poteau réussis. |

Rapports locaux :

- `resource-cell-qualification-652ce93849f64955b8511a0bc93bc404.json`
- `charted-resource-qualification-79b09b9c3e27461482da320447538bf2.json`
- `silo-cell-qualification-6cf7633b702c42bb95f088148fdd0f0f.json`
- `silo-cell-qualification-301a61b1352a43c88793cb77d7b8e107.json`

Le premier passage de la fixture de ressources a échoué sur une assertion ancienne qui interdisait les rôles `link-n`. L'assertion vérifie désormais les cinq équipements principaux et exige un plan pour tous les rôles enregistrés, liaisons comprises. Le passage suivant a réussi.

Le choix d'une cellule hors zone de mort et la frontière de troncature sont prouvés par les tests hors ligne, pas par ces quatre fixtures. La protection supplémentaire de la maintenance contre les zones de mort a été ajoutée après ces essais et vérifiée hors ligne. La reprise du silo en construction utilise un registre explicitement marqué `building` après destruction du poteau ; elle ne prétend pas reproduire une panne de processus.

## Courte reprise de campagne normale

La campagne existante de graine **20261011** a repris avec son monde et ses stocks conservés, sans préparation de fixture. La tentative du 1er octobre, de 09:59:17 à 10:02:17 UTC, visait `steel-processing`, avec une limite de trois minutes et une tentative d'objectif. Le modèle principal est resté `glm-5.3-flash` via l'API Ollama Cloud ; le profil privé existant avait Nimble activé en ombre uniquement.

L'arrêt est `wall-clock-budget`, pas une réussite d'objectif. La réconciliation native a vérifié 82 opérations, dont une attente annulée, sans rejouer de mutation. Le registre stratégique ne garde plus de tentative en attente. Le serveur a ensuite sauvegardé et terminé au tick **456 579**. La recherche n'est pas certifiée achevée par cette tentative ; le prochain lancement doit observer l'état conservé.

Les deux serveurs de fixture ont également été sauvegardés et arrêtés par les commandes du projet. Les trois campagnes autonomes jusqu'à la fusée restent à qualifier.
