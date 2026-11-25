# Passload

Partagez un ou plusieurs fichiers depuis Windows, Linux ou macOS avec un lien temporaire. Le destinataire ouvre le lien dans son navigateur et télécharge un fichier ou toute la sélection en ZIP. L’appareil expéditeur garde les originaux.

**[Télécharger Passload pour Windows x64](https://github.com/mertioz/passload/releases/latest/download/Passload.exe)** · [Archives Linux et macOS, versions et empreintes SHA-256](https://github.com/mertioz/passload/releases)

![Fenêtre principale de Passload](docs/passload.png)

## Envoyer sous Windows

1. Lancez `Passload.exe`. Déposez les fichiers dans la fenêtre ou choisissez **Parcourir les fichiers**.
2. Choisissez la durée et cliquez sur **Créer le lien**. Le type proposé est **WebRTC · IP publique**. Envoyez l’adresse complète à votre destinataire. Les liens publics commencent par `https://transfer.aymen.site/`.
3. Gardez le PC allumé et connecté, et les fichiers à leur emplacement. Vous pouvez fermer la fenêtre de Passload.

**Liens → Ouvrir ici** ouvre un aperçu sur le PC expéditeur. **Modifier le lien** permet de changer le titre et la description que le destinataire voit sur la page. L’adresse reste utilisable. **Arrêter le partage** interrompt les transferts et désactive le lien. L’expiration met aussi fin au partage.

L’exécutable Windows vérifie les versions GitHub au lancement et toutes les heures. Le bouton **Vérifier les mises à jour** se trouve dans **Connexion**. Passload vérifie l’empreinte SHA-256 du fichier publié et attend la fin des opérations en cours avant de remplacer l’exécutable.

### Partager en arrière-plan

Fermer la fenêtre range Passload près de l’horloge tant qu’un partage ou un transfert est en cours. Chaque lien garde sa propre expiration. Quand le dernier lien et le dernier transfert sont terminés, Passload se ferme automatiquement si la fenêtre est masquée. Double-cliquez sur l’icône ou relancez l’exécutable pour retrouver la même session. **Quitter Passload**, dans le menu de l’icône, arrête tous les liens.

Passload empêche la veille automatique de Windows pendant un partage actif. Une mise en veille volontaire ou une coupure du réseau interrompt l’accès pendant cette période. Les liens cessent de fonctionner lorsque le PC s’éteint. Si le port préféré est occupé, un partage automatique peut utiliser un autre port libre.

### Suivre les téléchargements

Dans **Liens**, chaque partage affiche ses transferts terminés et le nombre d’IP distinctes observées. Le détail contient le fichier ou le ZIP, la date, l’adresse IP visible et le mode de transfert. **Historique** permet de retrouver les téléchargements après fermeture ou expiration.

Une IP peut être partagée par plusieurs personnes ou changer. Les pages ouvertes, les aperçus locaux et les transferts incomplets ne sont pas comptabilisés. Le journal reste sur le PC dans `%LOCALAPPDATA%\Passload\download-history-tls.jsonl` et garde les 5 000 derniers transferts des 30 derniers jours.

## Recevoir

Ouvrez le lien complet dans un navigateur récent ou collez-le dans **Recevoir** sur un deuxième PC Windows. Les boutons permettent de choisir un fichier ou **Tout télécharger en ZIP**. Le ZIP est construit au fil de l’envoi et les fichiers homonymes conservent des entrées distinctes.

## WebRTC · IP publique

Le lien ouvre la page statique `transfer.aymen.site` en HTTPS. Les appareils utilisent le service public Piping Server pour échanger des messages de connexion chiffrés. STUN aide WebRTC à découvrir les chemins disponibles. Les fichiers passent directement entre les appareils. Si WebRTC est bloqué, le bouton **Ouvrir le téléchargement HTTPS** ouvre la page servie par le PC expéditeur. [WebRTC](https://www.w3.org/TR/webrtc/) · [Piping Server](https://github.com/nwtgck/piping-server)

Passload demande à la box une redirection TCP temporaire par UPnP ou NAT-PMP et la renouvelle pendant le partage. Sur une connexion où l’opérateur limite les ports disponibles, Passload essaie d’autres plages. Le secours HTTPS utilise l’adresse publique et le port accepté par la box. **Arrêter le partage** libère la redirection après le dernier lien qui l’utilise.

Le port local par défaut est **47831**. Windows peut demander une autorisation de pare-feu pour Passload lors du premier partage public. **Connexion → Réglages avancés** permet aussi de préciser une redirection manuelle, un nom de domaine ou les réglages du pare-feu.

Le mode principal nécessite une adresse IP publique joignable. Un opérateur ou un autre routeur peut bloquer les connexions entrantes. Le runtime Microsoft Edge WebView2 est nécessaire sur le PC qui envoie via WebRTC. Le téléchargement de secours HTTPS reste accessible si le runtime est absent.

L’adresse HTTPS du PC peut utiliser un certificat créé localement. Le navigateur peut alors afficher un avertissement lors du secours HTTPS. **Recevoir** vérifie l’empreinte SHA-256 incluse dans le lien et permet de télécharger dans l’application. Les téléchargements individuels HTTPS acceptent les requêtes de reprise.

Pour que le navigateur reconnaisse aussi le certificat du PC, renseignez votre domaine dans **Connexion → Réglages avancés → Nom de domaine pour le certificat HTTPS**. Son DNS doit pointer vers votre adresse publique et le magasin personnel de votre utilisateur Windows doit contenir un certificat serveur valide avec sa clé privée. Le téléchargement HTTPS utilise ce domaine avec le port accordé par la box.

Vous pouvez obtenir le certificat d’un domaine via une validation DNS, y compris lorsque votre box refuse les ports 80 et 443. Lors d’un renouvellement, installez le nouveau certificat dans le même magasin. Passload le sélectionne la prochaine fois qu’il démarre son serveur de partage. [Validation DNS chez Let’s Encrypt](https://letsencrypt.org/docs/challenge-types/#dns-01-challenge)

## Autres types de lien

**HTTPS · IP publique** ouvre la page commune puis donne accès au téléchargement HTTPS depuis le PC. **Réseau local** crée un lien utilisable sur le même réseau, avec l’adresse locale de l’expéditeur.

L’onglet **Recevoir** ouvre aussi les anciens liens GitHub Pages avec le lecteur intégré à Passload.

## Linux et macOS

Extrayez l’archive correspondant à votre architecture. Dans le terminal, lancez `./passload document.pdf photo.jpg` pour partager en HTTPS ou ajoutez `--local` pour le réseau local. Laissez le terminal ouvert pendant le partage. Pour recevoir avec vérification de l’empreinte, utilisez `./passload --receive 'LIEN' destination`. Avec plusieurs fichiers, `destination` reçoit le ZIP. `./passload --help` affiche les options.

## Compiler et vérifier

Sous Windows x64 avec le SDK .NET 10 et le runtime Microsoft Edge WebView2, exécutez les commandes suivantes à la racine du dépôt. Le programme distribué inclut le runtime .NET.

```powershell
dotnet build Passload.slnx -c Release
dotnet run -c Release --project tests/Passload.Checks
dotnet publish src/Passload/Passload.csproj -c Release -r win-x64 -p:PublishProfile=Portable -o artifacts/release
```

Les tests locaux vérifient notamment l’expiration, la révocation, les réponses HTTPS, le ZIP et les redirections simulées. L’option `--network` du programme de vérification teste aussi l’interface réseau et l’ouverture temporaire d’un port avec un fichier de démonstration. Le workflow **Versions Linux et macOS** construit les archives x64 et arm64 et teste un transfert sur chaque système.

Le workflow manuel **Partage par IP publique** vérifie un lien de démonstration depuis un autre réseau. Il bloque les téléchargements HTTPS pendant le test WebRTC, puis désactive WebRTC pour vérifier le secours sur la même adresse.

## Licence

Le code de Passload est publié sous [licence MIT](LICENSE). Les notices des bibliothèques et de la police sont disponibles dans le [dossier des licences](licenses) et dans **Connexion → Licences du logiciel**.
