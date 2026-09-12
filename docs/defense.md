# Première boucle de défense C#

La commande `defend --session <manifest> --seconds 60` exécute un contrôleur séquentiel, pour une durée de 1 à 3600 secondes. Il ne demande aucun objectif au LLM et ne réalise pas encore la progression de production.

## Décision et préemption

Le host valide une projection de l'observation native : identité, tick unique de collecte, contrôle IA ou manuel, état du personnage, arrêt confirmé, arme sélectionnée et ennemis actuellement visibles. Il refuse une observation incomplète sur ces champs, incohérente ou ancienne. Les ennemis vides sérialisés comme table Lua vide sont acceptés.

La politique choisit le plus proche ennemi observé dans la portée courante d'une arme à balles prête. Les égalités sont résolues par identifiant. Elle n'ordonne aucun tir lorsque le personnage est mort, en contrôle manuel, sans arme compatible, hors portée ou en attente de confirmation d'arrêt natif.

Si une action précédente possède encore le personnage, le contrôleur écrit une intention d'annulation, attend le reçu terminal, puis recommence par une observation. Le nouveau tir utilise ce scope actualisé et une précondition de position. Une action de tir dure au plus 60 ticks, avec une deadline de 180 ticks. Le moteur revérifie visibilité et portée de fonctionnement de l'arme ; le reçu mesure les effets. La boucle observe de nouveau après chaque résolution.

Un verrou de session conserve la possession du personnage entre les appels. Les autres processus host peuvent observer mais ne peuvent pas envoyer de mutations concurrentes. Le scope moteur et le bouton manuel restent l'autorité lors d'un changement de pilote. Le verrou ne prétend pas protéger d'un administrateur utilisant directement la console ou RCON.

## Résultats inconnus et journal

Chaque intention est écrite dans un journal JSONL avec flush disque avant sa transmission. Une réponse perdue conserve l'identifiant de l'opération ; la boucle consulte le reçu avant d'envisager une autre commande. Elle ne retransmet pas automatiquement la soumission. Un reçu absent impose une réconciliation et arrête ce contrôleur initial.

Les interruptions de transport peuvent suspendre les décisions jusqu'à une nouvelle lecture. La boucle ne peut pas agir sans liaison au moteur ; la deadline Lua borne la dernière action envoyée. À son arrêt normal, elle consulte puis annule, si nécessaire, sa propre action restante. Le journal JSONL est provisoire : reprise SQLite, objectifs persistants et réconciliation complète après redémarrage restent à développer.

## Preuves et limites

`verify-defense` exige une session fixture et la marque avant les préparations. Il équipe le personnage, restaure sa santé, lance une attente et crée un petit déchiqueteur avec un ordre natif d'attaque. La cible n'est pas fournie à la politique C# : elle doit être découverte par observation. Les lectures indépendantes vérifient annulation du travail, balles consommées, survie, disparition de l'ennemi et conservation du personnage.

Le test a réussi sans joueur et avec le pilote connecté au même avatar. Les mesures et les autres validations figurent dans [validation.md](validation.md). Les tests hors ligne couvrent notamment perte de réponse lors du tir ou de l'annulation, changement vers contrôle manuel après préemption, données anciennes et impossibilité de persister une intention.

La qualification effectue également une lecture de photographie d'usine en parallèle de la réaction. Sur la fixture de 230 coffres avec pilote, 507 enregistrements ont été récupérés pendant le combat. Les appels de contrôle passent devant les pages en attente du même client de session ; l'appel natif déjà engagé n'est pas interrompu.

La fuite, les tirs en mouvement, le choix d'équipement, le réapprovisionnement, la protection des bâtiments et les attaques multiples sous charge restent à développer ou qualifier. La politique courante n'est pas une stratégie de survie complète. Elle n'effectue pas automatiquement la reprise du travail interrompu ; celle-ci appartient au futur planificateur après réconciliation des effets.
