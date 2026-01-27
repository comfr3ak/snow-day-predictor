export async function getVersion() {
    try {
        const response = await fetch('version.json');
        const data = await response.json();
        return data.version;
    } catch (error) {
        console.error('Version load error:', error);
        return '2026.01.27.1640';
    }
}
